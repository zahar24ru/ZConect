package admin

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"zconect/server/internal/logging"
)

// ─── Mock services ──────────────────────────────────────────────

type mockSessionCounter struct{ count int }

func (m *mockSessionCounter) ActiveCount() int { return m.count }

type mockPeerCounter struct{ peers, rooms int }

func (m *mockPeerCounter) PeerCount() int { return m.peers }
func (m *mockPeerCounter) RoomCount() int { return m.rooms }

type mockWebRTCProvider struct{ sessions []WebRTCSessionInfo }

func (m *mockWebRTCProvider) ListActive() []WebRTCSessionInfo { return m.sessions }

type mockWebRTCKiller struct {
	killed []string
	found  bool
}

func (m *mockWebRTCKiller) KillSession(id string) bool {
	m.killed = append(m.killed, id)
	return m.found
}

// newTestLogger — logging.Logger с temp file path, для тестов.
func newTestLogger(t *testing.T) *logging.Logger {
	t.Helper()
	f, err := os.CreateTemp("", "admin_test_log_*")
	if err != nil {
		t.Fatalf("temp log: %v", err)
	}
	path := f.Name()
	f.Close()
	lg, err := logging.New(path)
	if err != nil {
		os.Remove(path)
		t.Fatalf("logging.New: %v", err)
	}
	t.Cleanup(func() { os.Remove(path) })
	return lg
}

// ─── Test setup helper ──────────────────────────────────────────

// newTestHandler — создаёт Handler с temp audit log, тестовым паролем "test-pass".
// Returns handler + password + cleanup func.
func newTestHandler(t *testing.T) (*Handler, string, func()) {
	t.Helper()

	tmp, err := os.MkdirTemp("", "admin_test_*")
	if err != nil {
		t.Fatalf("mkdtemp: %v", err)
	}
	auditPath := filepath.Join(tmp, "audit.log")

	password := "test-pass-12345"
	hash, err := HashPassword(password)
	if err != nil {
		os.RemoveAll(tmp)
		t.Fatalf("HashPassword: %v", err)
	}

	cfg := Config{
		PasswordHash:   hash,
		AuditLogPath:   auditPath,
		SessionIdleTTL: time.Hour,
		SessionMaxLife: 8 * time.Hour,
		CookieSecure:   false, // тесты не через HTTPS
	}
	svc := Services{
		Sessions: &mockSessionCounter{count: 3},
		Peers:    &mockPeerCounter{peers: 5, rooms: 2},
	}
	logger := newTestLogger(t)
	h, err := NewHandler(cfg, svc, logger)
	if err != nil {
		os.RemoveAll(tmp)
		t.Fatalf("NewHandler: %v", err)
	}

	cleanup := func() {
		os.RemoveAll(tmp)
	}
	return h, password, cleanup
}

// login — помощник: делает POST /admin/api/login и возвращает session cookie + CSRF token.
// Используется для authed requests в последующих tests.
func login(t *testing.T, h *Handler, password string) (sessionCookie *http.Cookie, csrfToken string) {
	t.Helper()
	body, _ := json.Marshal(map[string]string{"password": password})
	req := httptest.NewRequest("POST", "/admin/api/login", bytes.NewReader(body))
	req.RemoteAddr = "127.0.0.1:12345"
	req.Header.Set("Content-Type", "application/json")
	rec := httptest.NewRecorder()
	h.apiLogin(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("login failed: %d %s", rec.Code, rec.Body.String())
	}
	for _, c := range rec.Result().Cookies() {
		if c.Name == sessionCookieName {
			sessionCookie = c
		}
	}
	var resp map[string]any
	json.Unmarshal(rec.Body.Bytes(), &resp)
	if tok, ok := resp["csrf_token"].(string); ok {
		csrfToken = tok
	}
	if sessionCookie == nil {
		t.Fatalf("login response missing session cookie")
	}
	if csrfToken == "" {
		t.Fatalf("login response missing csrf_token")
	}
	return
}

// authedRequest — обёртка для POST/GET с auth cookie + CSRF token.
func authedRequest(h *Handler, method, path, body string, cookie *http.Cookie, csrf string) *httptest.ResponseRecorder {
	var rdr io.Reader
	if body != "" {
		rdr = strings.NewReader(body)
	}
	req := httptest.NewRequest(method, path, rdr)
	req.RemoteAddr = "127.0.0.1:12345"
	req.Header.Set("Content-Type", "application/json")
	if cookie != nil {
		req.AddCookie(cookie)
	}
	if csrf != "" {
		req.Header.Set("X-CSRF-Token", csrf)
	}
	rec := httptest.NewRecorder()
	// Resolve handler через Register'нутый mux
	mux := http.NewServeMux()
	h.Register(mux)
	mux.ServeHTTP(rec, req)
	return rec
}

// ─── Login tests ──────────────────────────────────────────

func TestAPILogin_WrongPassword(t *testing.T) {
	h, _, cleanup := newTestHandler(t)
	defer cleanup()

	body, _ := json.Marshal(map[string]string{"password": "wrong-password"})
	req := httptest.NewRequest("POST", "/admin/api/login", bytes.NewReader(body))
	req.RemoteAddr = "127.0.0.1:12345"
	req.Header.Set("Content-Type", "application/json")
	rec := httptest.NewRecorder()
	h.apiLogin(rec, req)

	if rec.Code != http.StatusUnauthorized {
		t.Errorf("wrong pass: code=%d, want 401", rec.Code)
	}
	// Session cookie НЕ должен ставиться при ошибке
	for _, c := range rec.Result().Cookies() {
		if c.Name == sessionCookieName {
			t.Error("cookie не должен ставиться при неверном пароле")
		}
	}
}

func TestAPILogin_CorrectPassword_SetsCookie(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, csrf := login(t, h, password)
	if cookie.HttpOnly != true {
		t.Error("session cookie должен быть HttpOnly")
	}
	if cookie.Value == "" {
		t.Error("session cookie value пустой")
	}
	if csrf == "" {
		t.Error("CSRF token empty")
	}
}

func TestAPILogin_RateLimitAfter5Attempts(t *testing.T) {
	h, _, cleanup := newTestHandler(t)
	defer cleanup()

	body, _ := json.Marshal(map[string]string{"password": "wrong"})
	makeRequest := func() int {
		req := httptest.NewRequest("POST", "/admin/api/login", bytes.NewReader(body))
		req.RemoteAddr = "192.168.99.99:555"
		req.Header.Set("Content-Type", "application/json")
		rec := httptest.NewRecorder()
		h.apiLogin(rec, req)
		return rec.Code
	}

	// 5 wrong попыток — все возвращают 401
	for i := 0; i < 5; i++ {
		if code := makeRequest(); code != http.StatusUnauthorized {
			t.Errorf("attempt %d: code=%d, want 401", i+1, code)
		}
	}
	// 6-й — rate limited (429)
	if code := makeRequest(); code != http.StatusTooManyRequests {
		t.Errorf("rate limit hit: code=%d, want 429", code)
	}
}

func TestAPILogin_MethodNotAllowed(t *testing.T) {
	h, _, cleanup := newTestHandler(t)
	defer cleanup()

	req := httptest.NewRequest("GET", "/admin/api/login", nil)
	rec := httptest.NewRecorder()
	h.apiLogin(rec, req)

	if rec.Code != http.StatusMethodNotAllowed {
		t.Errorf("GET на login: code=%d, want 405", rec.Code)
	}
}

// ─── Session + Dashboard ──────────────────────────────────────────

func TestDashboard_UnauthenticatedDenied(t *testing.T) {
	h, _, cleanup := newTestHandler(t)
	defer cleanup()

	req := httptest.NewRequest("GET", "/admin", nil)
	req.RemoteAddr = "127.0.0.1:11111"
	rec := authedRequest(h, "GET", "/admin", "", nil, "")
	_ = req

	// redirect на login (307 typical)
	if rec.Code != http.StatusSeeOther && rec.Code != http.StatusFound &&
		rec.Code != http.StatusTemporaryRedirect && rec.Code != http.StatusUnauthorized {
		t.Errorf("unauth dashboard: code=%d, want redirect or 401", rec.Code)
	}
}

func TestDashboard_AuthenticatedReturnsHTML(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, _ := login(t, h, password)

	req := httptest.NewRequest("GET", "/admin", nil)
	req.RemoteAddr = "127.0.0.1:12345"
	req.AddCookie(cookie)
	rec := httptest.NewRecorder()
	mux := http.NewServeMux()
	h.Register(mux)
	mux.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Errorf("auth dashboard: code=%d, want 200, body=%s", rec.Code, rec.Body.String())
	}
	ct := rec.Header().Get("Content-Type")
	if !strings.HasPrefix(ct, "text/html") {
		t.Errorf("Content-Type=%q, want text/html", ct)
	}
}

func TestAPISessionInfo_ReturnsCSRFToken(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, csrf := login(t, h, password)

	rec := authedRequest(h, "GET", "/admin/api/session", "", cookie, csrf)
	if rec.Code != http.StatusOK {
		t.Fatalf("session info: code=%d body=%s", rec.Code, rec.Body.String())
	}
	var resp map[string]any
	json.Unmarshal(rec.Body.Bytes(), &resp)
	if resp["csrf_token"] != csrf {
		t.Errorf("csrf_token mismatch: got %v, want %s", resp["csrf_token"], csrf)
	}
	if _, ok := resp["created_at"].(string); !ok {
		t.Error("created_at missing")
	}
}

// ─── Logout ──────────────────────────────────────────

func TestAPILogout_DestroysSession(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, csrf := login(t, h, password)

	rec := authedRequest(h, "POST", "/admin/api/logout", "", cookie, csrf)
	if rec.Code != http.StatusOK {
		t.Fatalf("logout: code=%d body=%s", rec.Code, rec.Body.String())
	}

	// После logout'а session должна быть destroyed — session info endpoint
	// возвращает 302 (redirect на login) или 401 в зависимости от middleware.
	rec2 := authedRequest(h, "GET", "/admin/api/session", "", cookie, csrf)
	if rec2.Code != http.StatusUnauthorized &&
		rec2.Code != http.StatusFound &&
		rec2.Code != http.StatusSeeOther &&
		rec2.Code != http.StatusTemporaryRedirect {
		t.Errorf("после logout session info: code=%d, want redirect or 401", rec2.Code)
	}
}

// ─── CSRF protection ──────────────────────────────────────────

func TestCSRF_POSTWithoutTokenRejected(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, _ := login(t, h, password)

	// POST БЕЗ CSRF token — должен быть 403
	rec := authedRequest(h, "POST", "/admin/api/logout", "", cookie, "")
	if rec.Code != http.StatusForbidden {
		t.Errorf("POST без CSRF: code=%d, want 403", rec.Code)
	}
}

func TestCSRF_WrongTokenRejected(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, _ := login(t, h, password)

	rec := authedRequest(h, "POST", "/admin/api/logout", "", cookie, "wrong-csrf-token")
	if rec.Code != http.StatusForbidden {
		t.Errorf("POST с неверным CSRF: code=%d, want 403", rec.Code)
	}
}

// ─── Health ──────────────────────────────────────────

func TestAPIHealth_ReturnsServerStats(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, csrf := login(t, h, password)

	rec := authedRequest(h, "GET", "/admin/api/health", "", cookie, csrf)
	if rec.Code != http.StatusOK {
		t.Fatalf("health: code=%d body=%s", rec.Code, rec.Body.String())
	}
	var resp healthResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &resp); err != nil {
		t.Fatalf("json: %v", err)
	}
	if resp.ActiveSessions != 3 {
		t.Errorf("ActiveSessions=%d, want 3 (mock value)", resp.ActiveSessions)
	}
	if resp.ActivePeers != 5 {
		t.Errorf("ActivePeers=%d, want 5", resp.ActivePeers)
	}
	if resp.ActiveRooms != 2 {
		t.Errorf("ActiveRooms=%d, want 2", resp.ActiveRooms)
	}
	if resp.AdminSessions != 1 {
		t.Errorf("AdminSessions=%d, want 1 (our session)", resp.AdminSessions)
	}
	if resp.GoVersion == "" {
		t.Error("GoVersion empty")
	}
	if resp.Goroutines < 1 {
		t.Errorf("Goroutines=%d, want >=1", resp.Goroutines)
	}
}

// ─── Password change ──────────────────────────────────────────

func TestAPIChangePassword_WrongCurrentRejected(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, csrf := login(t, h, password)

	body := `{"old":"wrong-current","new":"new-password-123"}`
	rec := authedRequest(h, "POST", "/admin/api/password", body, cookie, csrf)
	if rec.Code != http.StatusUnauthorized {
		t.Errorf("wrong current pass: code=%d, want 401 body=%s", rec.Code, rec.Body.String())
	}
}

func TestAPIChangePassword_Success(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, csrf := login(t, h, password)

	newPass := "new-better-password-456"
	body, _ := json.Marshal(map[string]string{"old": password, "new": newPass})
	rec := authedRequest(h, "POST", "/admin/api/password", string(body), cookie, csrf)
	if rec.Code != http.StatusOK {
		t.Fatalf("change pass: code=%d body=%s", rec.Code, rec.Body.String())
	}

	// Новый пароль должен работать для login'а в новом session
	body2, _ := json.Marshal(map[string]string{"password": newPass})
	req := httptest.NewRequest("POST", "/admin/api/login", bytes.NewReader(body2))
	req.RemoteAddr = "127.0.0.1:22222"
	req.Header.Set("Content-Type", "application/json")
	rec2 := httptest.NewRecorder()
	h.apiLogin(rec2, req)

	if rec2.Code != http.StatusOK {
		t.Errorf("login с новым паролем: code=%d, want 200", rec2.Code)
	}

	// Старый пароль больше не работает
	body3, _ := json.Marshal(map[string]string{"password": password})
	req3 := httptest.NewRequest("POST", "/admin/api/login", bytes.NewReader(body3))
	req3.RemoteAddr = "127.0.0.2:22222"
	req3.Header.Set("Content-Type", "application/json")
	rec3 := httptest.NewRecorder()
	h.apiLogin(rec3, req3)

	if rec3.Code != http.StatusUnauthorized {
		t.Errorf("старый пароль: code=%d, want 401", rec3.Code)
	}
}

func TestAPIChangePassword_TooShortRejected(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, csrf := login(t, h, password)

	body, _ := json.Marshal(map[string]string{"old": password, "new": "short"})
	rec := authedRequest(h, "POST", "/admin/api/password", string(body), cookie, csrf)
	if rec.Code != http.StatusBadRequest {
		t.Errorf("слишком короткий: code=%d, want 400 body=%s", rec.Code, rec.Body.String())
	}
}

// ─── Admin sessions management ──────────────────────────────────────────

func TestAPISessions_ListsCurrentSession(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, csrf := login(t, h, password)

	rec := authedRequest(h, "GET", "/admin/api/sessions", "", cookie, csrf)
	if rec.Code != http.StatusOK {
		t.Fatalf("list sessions: %d body=%s", rec.Code, rec.Body.String())
	}
	var resp map[string]any
	json.Unmarshal(rec.Body.Bytes(), &resp)
	sessions, ok := resp["sessions"].([]any)
	if !ok || len(sessions) != 1 {
		t.Errorf("ожидалась 1 session, got %v", resp["sessions"])
	}
}

func TestAPILogoutAllOthers_KeepsCurrent(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	// Создаём 3 сессии с разных IP
	cookie1, csrf1 := login(t, h, password)
	login(t, h, password)
	login(t, h, password)

	// logout-all-others вызывается с cookie1 — должна остаться только она
	rec := authedRequest(h, "POST", "/admin/api/sessions/logout-all", "", cookie1, csrf1)
	if rec.Code != http.StatusOK {
		t.Fatalf("logout-all-others: %d body=%s", rec.Code, rec.Body.String())
	}

	h.sessions.mu.RLock()
	count := len(h.sessions.sessions)
	h.sessions.mu.RUnlock()
	if count != 1 {
		t.Errorf("после logout-all-others sessions count=%d, want 1 (только current)", count)
	}
}

// ─── Client version ──────────────────────────────────────────

func TestAPIVersion_GetWhenFileMissingReturnsDefault(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	// ClientVersionFile не задан в test config → GET должен вернуть 501 или 404 с графичным error
	cookie, csrf := login(t, h, password)

	rec := authedRequest(h, "GET", "/admin/api/version", "", cookie, csrf)
	// 501 = "client version file not configured" (ожидаемое поведение),
	// 200 = default empty response, 404 = missing file
	validCodes := map[int]bool{
		http.StatusOK: true, http.StatusNotFound: true, http.StatusNotImplemented: true,
	}
	if !validCodes[rec.Code] {
		t.Errorf("version get no-file: code=%d, want 200/404/501 body=%s", rec.Code, rec.Body.String())
	}
}

func TestAPIVersion_PutRequiresFile(t *testing.T) {
	// Handler с ClientVersionFile → PUT должен писать файл
	tmp, _ := os.MkdirTemp("", "admin_ver_*")
	defer os.RemoveAll(tmp)
	vfile := filepath.Join(tmp, "client-version.json")

	password := "test-pass-v"
	hash, _ := HashPassword(password)
	cfg := Config{
		PasswordHash:      hash,
		AuditLogPath:      filepath.Join(tmp, "audit.log"),
		SessionIdleTTL:    time.Hour,
		SessionMaxLife:    8 * time.Hour,
		ClientVersionFile: vfile,
	}
	h, _ := NewHandler(cfg, Services{
		Sessions: &mockSessionCounter{}, Peers: &mockPeerCounter{},
	}, newTestLogger(t))

	cookie, csrf := login(t, h, password)

	body := `{"latest_version":"1.2.3","download_url":"https://example.com/app.exe","release_notes":"test","min_compatible_version":"1.0.0"}`
	rec := authedRequest(h, "POST", "/admin/api/version", body, cookie, csrf)
	if rec.Code != http.StatusOK {
		t.Fatalf("put version: code=%d body=%s", rec.Code, rec.Body.String())
	}

	raw, err := os.ReadFile(vfile)
	if err != nil {
		t.Fatalf("file не создан: %v", err)
	}
	// JSON может быть pretty-printed (с отступами) — проверяем substring `"latest_version"`.`
	content := string(raw)
	if !strings.Contains(content, "1.2.3") || !strings.Contains(content, "latest_version") {
		t.Errorf("file не содержит ожидаемых полей: %s", content)
	}
}

// ─── Maintenance mode ──────────────────────────────────────────

func TestAPIMaintenance_Toggle(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, csrf := login(t, h, password)

	// Enable
	rec := authedRequest(h, "POST", "/admin/api/maintenance", `{"enabled":true,"message":"upgrade"}`, cookie, csrf)
	if rec.Code != http.StatusOK {
		t.Fatalf("enable: %d body=%s", rec.Code, rec.Body.String())
	}
	enabled, msg := h.IsMaintenance()
	if !enabled {
		t.Error("maintenance не включился")
	}
	if msg != "upgrade" {
		t.Errorf("message=%q, want 'upgrade'", msg)
	}

	// Disable
	rec2 := authedRequest(h, "POST", "/admin/api/maintenance", `{"enabled":false}`, cookie, csrf)
	if rec2.Code != http.StatusOK {
		t.Fatalf("disable: %d", rec2.Code)
	}
	if enabled2, _ := h.IsMaintenance(); enabled2 {
		t.Error("maintenance не выключился")
	}
}

// ─── WebRTC sessions tab ──────────────────────────────────────────

func TestAPIWebRTCSessions_ListsSessions(t *testing.T) {
	tmp, _ := os.MkdirTemp("", "admin_wrtc_*")
	defer os.RemoveAll(tmp)
	password := "test-wrtc"
	hash, _ := HashPassword(password)
	cfg := Config{
		PasswordHash: hash, AuditLogPath: filepath.Join(tmp, "audit.log"),
		SessionIdleTTL: time.Hour, SessionMaxLife: 8 * time.Hour,
	}
	// Inject mock с test sessions (включая одну с new brute-force fields)
	mockSessions := []WebRTCSessionInfo{
		{ID: "abc-1", LoginCode: "11111111", State: "PAIRING",
			CreatedAt: time.Now().UTC(), ExpiresAt: time.Now().Add(time.Hour).UTC(),
			TotalFailedAttempts: 0, LockoutTier: 0},
		{ID: "def-2", LoginCode: "22222222", State: "PAIRING",
			CreatedAt: time.Now().UTC(), ExpiresAt: time.Now().Add(time.Hour).UTC(),
			TotalFailedAttempts: 12, LockoutTier: 2}, // suspicious
		{ID: "ghi-3", LoginCode: "33333333", State: "BLOCKED",
			CreatedAt: time.Now().UTC(), ExpiresAt: time.Now().Add(time.Hour).UTC(),
			TotalFailedAttempts: 25, LockoutTier: 5}, // permanently blocked
	}
	svc := Services{
		Sessions:       &mockSessionCounter{},
		Peers:          &mockPeerCounter{},
		WebRTCSessions: &mockWebRTCProvider{sessions: mockSessions},
	}
	h, _ := NewHandler(cfg, svc, newTestLogger(t))

	cookie, csrf := login(t, h, password)

	rec := authedRequest(h, "GET", "/admin/api/webrtc-sessions", "", cookie, csrf)
	if rec.Code != http.StatusOK {
		t.Fatalf("webrtc-sessions: %d body=%s", rec.Code, rec.Body.String())
	}
	var resp struct {
		Sessions []WebRTCSessionInfo `json:"sessions"`
		Count    int                 `json:"count"`
	}
	json.Unmarshal(rec.Body.Bytes(), &resp)
	if resp.Count != 3 {
		t.Errorf("count=%d, want 3", resp.Count)
	}
	// Verify новые поля сериализуются
	blocked := resp.Sessions[2]
	if blocked.State != "BLOCKED" {
		t.Errorf("session[2].State=%s, want BLOCKED", blocked.State)
	}
	if blocked.LockoutTier != 5 {
		t.Errorf("session[2].LockoutTier=%d, want 5", blocked.LockoutTier)
	}
	if blocked.TotalFailedAttempts != 25 {
		t.Errorf("session[2].TotalFailedAttempts=%d, want 25", blocked.TotalFailedAttempts)
	}
}

func TestAPIKillWebRTCSession_CallsKiller(t *testing.T) {
	tmp, _ := os.MkdirTemp("", "admin_kill_*")
	defer os.RemoveAll(tmp)
	password := "test-kill"
	hash, _ := HashPassword(password)
	cfg := Config{
		PasswordHash: hash, AuditLogPath: filepath.Join(tmp, "audit.log"),
		SessionIdleTTL: time.Hour, SessionMaxLife: 8 * time.Hour,
	}
	killer := &mockWebRTCKiller{found: true}
	svc := Services{
		Sessions:     &mockSessionCounter{}, Peers: &mockPeerCounter{},
		WebRTCKiller: killer,
	}
	h, _ := NewHandler(cfg, svc, newTestLogger(t))

	cookie, csrf := login(t, h, password)

	rec := authedRequest(h, "POST", "/admin/api/webrtc-sessions/kill",
		`{"session_id":"victim-session-id"}`, cookie, csrf)
	if rec.Code != http.StatusOK {
		t.Fatalf("kill: %d body=%s", rec.Code, rec.Body.String())
	}
	if len(killer.killed) != 1 || killer.killed[0] != "victim-session-id" {
		t.Errorf("killer calls=%v, want [victim-session-id]", killer.killed)
	}
}

// ─── Audit log ──────────────────────────────────────────

func TestAPIAudit_ReturnsRecentEntries(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, csrf := login(t, h, password)

	// Login уже произвёл audit entry. Проверяем read.
	rec := authedRequest(h, "GET", "/admin/api/audit", "", cookie, csrf)
	if rec.Code != http.StatusOK {
		t.Fatalf("audit read: %d body=%s", rec.Code, rec.Body.String())
	}
	// Response: {"entries": [...], "total_file": N} — не "count"
	var resp struct {
		Entries   []map[string]any `json:"entries"`
		TotalFile int              `json:"total_file"`
	}
	json.Unmarshal(rec.Body.Bytes(), &resp)
	if len(resp.Entries) < 1 {
		t.Errorf("audit entries=%d, want >=1", len(resp.Entries))
	}
	// Проверяем что login entry присутствует
	foundLogin := false
	for _, e := range resp.Entries {
		if e["action"] == "login" && e["result"] == "ok" {
			foundLogin = true
			break
		}
	}
	if !foundLogin {
		t.Errorf("login entry не найден в audit log. Entries: %v", resp.Entries)
	}
}

// ─── Reset rate limit ──────────────────────────────────────────

func TestAPIResetRateLimit_ClearsBucket(t *testing.T) {
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	// Исчерпаем login rate limit для какого-то IP
	body, _ := json.Marshal(map[string]string{"password": "wrong"})
	for i := 0; i < 6; i++ {
		req := httptest.NewRequest("POST", "/admin/api/login", bytes.NewReader(body))
		req.RemoteAddr = "50.50.50.50:5050"
		req.Header.Set("Content-Type", "application/json")
		h.apiLogin(httptest.NewRecorder(), req)
	}

	// Admin делает reset
	cookie, csrf := login(t, h, password)
	rec := authedRequest(h, "POST", "/admin/api/ratelimit/reset", "", cookie, csrf)
	if rec.Code != http.StatusOK {
		t.Fatalf("reset: %d body=%s", rec.Code, rec.Body.String())
	}

	// Теперь тот же IP может снова login'ить (не 429)
	body2, _ := json.Marshal(map[string]string{"password": password})
	req := httptest.NewRequest("POST", "/admin/api/login", bytes.NewReader(body2))
	req.RemoteAddr = "50.50.50.50:5050"
	req.Header.Set("Content-Type", "application/json")
	rec2 := httptest.NewRecorder()
	h.apiLogin(rec2, req)
	if rec2.Code == http.StatusTooManyRequests {
		t.Error("после reset rate limit должен быть снят, но всё ещё 429")
	}
}

// ─── IP ban via handler ──────────────────────────────────────────

func TestAPIBan_AddAndList(t *testing.T) {
	resetBanState()
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, csrf := login(t, h, password)

	// Add ban
	rec := authedRequest(h, "POST", "/admin/api/ban",
		`{"ip":"9.9.9.9","reason":"brute-force","action":"add"}`, cookie, csrf)
	if rec.Code != http.StatusOK {
		t.Fatalf("ban add: %d body=%s", rec.Code, rec.Body.String())
	}

	// Проверим через IsIPBanned
	banned, reason := IsIPBanned("9.9.9.9")
	if !banned {
		t.Error("IP не забанен после apiBanIP add")
	}
	if reason != "brute-force" {
		t.Errorf("reason=%q, want brute-force", reason)
	}

	// List
	rec2 := authedRequest(h, "GET", "/admin/api/ban/list", "", cookie, csrf)
	if rec2.Code != http.StatusOK {
		t.Fatalf("ban list: %d", rec2.Code)
	}
	var resp struct {
		Bans  []BanEntry `json:"bans"`
		Count int        `json:"count"`
	}
	json.Unmarshal(rec2.Body.Bytes(), &resp)
	if resp.Count != 1 {
		t.Errorf("ban count=%d, want 1", resp.Count)
	}
}

func TestAPIBan_Remove(t *testing.T) {
	resetBanState()
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, csrf := login(t, h, password)

	// Add then remove
	authedRequest(h, "POST", "/admin/api/ban",
		`{"ip":"8.8.8.8","reason":"test","action":"add"}`, cookie, csrf)

	rec := authedRequest(h, "POST", "/admin/api/ban",
		`{"ip":"8.8.8.8","action":"remove"}`, cookie, csrf)
	if rec.Code != http.StatusOK {
		t.Fatalf("ban remove: %d body=%s", rec.Code, rec.Body.String())
	}

	banned, _ := IsIPBanned("8.8.8.8")
	if banned {
		t.Error("IP всё ещё banned после remove")
	}
}

func TestAPIBan_InvalidIPRejected(t *testing.T) {
	resetBanState()
	h, password, cleanup := newTestHandler(t)
	defer cleanup()

	cookie, csrf := login(t, h, password)

	rec := authedRequest(h, "POST", "/admin/api/ban",
		`{"ip":"not-an-ip","reason":"test","action":"add"}`, cookie, csrf)
	if rec.Code != http.StatusBadRequest {
		t.Errorf("invalid IP: code=%d, want 400", rec.Code)
	}
}

// Helper: drain response body не нужен для stdlib httptest, но оставляем на случай
var _ = io.Discard
