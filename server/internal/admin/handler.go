package admin

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	neturl "net/url"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"zconect/server/internal/logging"
	"zconect/server/internal/realip"
)

//go:embed login.html
var loginHTML []byte

//go:embed dashboard.html
var dashboardHTML []byte

// Session cookie name — prefixed with __Host- so browser enforces:
//   - Secure attribute (HTTPS only)
//   - No Domain attribute (exact origin only)
//   - Path=/ (mandatory for __Host-)
// BUT __Host- requires Path=/, а мы хотим Path=/admin — используем обычное имя,
// явно выставляем Secure/HttpOnly/SameSite=Strict ниже.
const sessionCookieName = "zc_admin_sess"

// Config — параметры, которые Handler получает при создании.
type Config struct {
	PasswordHash      string        // bcrypt hash из env; bootstrap only, runtime hash в h.passwordHash
	PasswordHashFile  string        // путь к persistent hash file (override env если exists)
	AllowedIPs        []string      // env ADMIN_ALLOWED_IPS; empty = allow all
	SessionIdleTTL    time.Duration // idle timeout, default 1h
	SessionMaxLife    time.Duration // absolute, default 8h
	AuditLogPath      string        // путь к audit log; empty = no audit
	AuditLogMaxMB     int           // rotate threshold MB (default 10)
	AuditLogKeep      int           // old files to keep (default 5)
	ClientVersionFile string        // путь к client-version.json (для Phase 3)
	TelemetryDBPath   string        // путь к telemetry.db (для backup download)
	TrustedProxies    []string      // IPs which may set X-Forwarded-For
	CookieSecure      bool          // true для prod (Secure flag); false для local dev
}

// Services — зависимости от других пакетов (stats из telemetry etc.).
// Интерфейсы, чтобы не импортировать конкретные packages и не делать circular deps.
type Services struct {
	Sessions       SessionCounter
	Peers          PeerCounter
	WebRTCSessions WebRTCSessionsProvider // опционально; для Active Sessions tab
	WebRTCKiller   WebRTCSessionKiller    // опционально; для force-close
	SuspiciousIPs  SuspiciousIPProvider   // опционально; для "Подозрительные IP" tab
}

type SessionCounter interface {
	ActiveCount() int
}
type PeerCounter interface {
	RoomCount() int
	PeerCount() int
}

// WebRTCSessionInfo — минимальный view на активную session (для admin panel).
type WebRTCSessionInfo struct {
	ID        string    `json:"id"`
	LoginCode string    `json:"login_code"` // короткий code (8 digits)
	State     string    `json:"state"`
	CreatedAt time.Time `json:"created_at"`
	ExpiresAt time.Time `json:"expires_at"`
	// JoinAttempts — counter текущего tier'а (ресетится при escalate)
	JoinAttempts int `json:"join_attempts"`
	// TotalFailedAttempts — cumulative count за всё время жизни сессии.
	// Показывает админу "эту сессию пытались 47 раз" независимо от tier.
	TotalFailedAttempts int `json:"total_failed_attempts"`
	// LockoutTier — 0 fresh, 1..4 progressive locks (15с/2m/15m/1h), 5+ = permanent blocked
	LockoutTier int `json:"lockout_tier"`
	// LockedUntilUnixSec — когда текущий lock истечёт (0 если не locked).
	// Unix seconds для простоты JS рендера (new Date(sec * 1000)).
	LockedUntilUnixSec int64 `json:"locked_until_unix_sec,omitempty"`
	PeerCount          int   `json:"peer_count"` // active peers в этой session
}

type WebRTCSessionsProvider interface {
	ListActive() []WebRTCSessionInfo
}

type WebRTCSessionKiller interface {
	// Close session by ID. Returns true if session был найден и закрыт.
	KillSession(sessionID string) bool
}

// SuspiciousIPProvider — источник per-IP enumeration statistics.
// Реализуется bruteforce.IPTracker; инжектится в Services для admin UI.
type SuspiciousIPProvider interface {
	Snapshot(minCount int) []SuspiciousIP
	WindowSec() int
}

// SuspiciousIP — публичное представление IPStat для admin API (json).
// Напрямую зеркалит bruteforce.IPStat — избегаем cross-package import cycle.
type SuspiciousIP struct {
	IP        string    `json:"ip"`
	Count     int       `json:"count"`
	FirstSeen time.Time `json:"first_seen"`
	LastSeen  time.Time `json:"last_seen"`
}

// Handler — главная структура admin package.
type Handler struct {
	cfg            Config
	svc            Services
	sessions       *SessionStore
	loginLimiter   *LoginRateLimiter
	ipWhitelist    *IPWhitelist
	audit          *AuditLog
	logger         *logging.Logger
	startedAt      time.Time
	realIP         *realip.Extractor

	// passwordMu защищает concurrent изменение passwordHash через ChangePassword.
	passwordMu   sync.RWMutex
	passwordHash string // actual current hash (может быть обновлён из persistent файла)

	// maintenanceMode — когда true, /api/v1/session/create возвращает 503.
	maintenanceMode atomic.Bool
	maintenanceMsg  atomic.Value // string
}

func NewHandler(cfg Config, svc Services, logger *logging.Logger) (*Handler, error) {
	if cfg.SessionIdleTTL == 0 {
		cfg.SessionIdleTTL = time.Hour
	}
	if cfg.SessionMaxLife == 0 {
		cfg.SessionMaxLife = 8 * time.Hour
	}
	audit, err := NewAuditLogWithConfig(cfg.AuditLogPath, AuditLogConfig{
		MaxMB:     cfg.AuditLogMaxMB,
		KeepCount: cfg.AuditLogKeep,
	})
	if err != nil {
		return nil, err
	}
	h := &Handler{
		cfg:            cfg,
		svc:            svc,
		sessions:       NewSessionStore(cfg.SessionIdleTTL, cfg.SessionMaxLife),
		loginLimiter:   NewLoginRateLimiter(5, 15*time.Minute),
		ipWhitelist:    NewIPWhitelist(cfg.AllowedIPs),
		audit:          audit,
		logger:         logger,
		startedAt:      time.Now().UTC(),
		realIP:         realip.New(cfg.TrustedProxies),
		passwordHash:   cfg.PasswordHash,
	}
	h.maintenanceMsg.Store("")

	// Persistent hash override: если есть файл с hash (от предыдущих ChangePassword),
	// используем его вместо env var. Иначе env из bootstrap.
	if cfg.PasswordHashFile != "" {
		if raw, err := os.ReadFile(cfg.PasswordHashFile); err == nil {
			s := strings.TrimSpace(string(raw))
			if s != "" {
				h.passwordHash = s
				logger.Log("INFO", "Admin", "password_hash_loaded_from_file",
					"using persisted hash from "+cfg.PasswordHashFile, logging.Entry{})
			}
		}
	}
	return h, nil
}

// currentPasswordHash — thread-safe getter для runtime hash.
func (h *Handler) currentPasswordHash() string {
	h.passwordMu.RLock()
	defer h.passwordMu.RUnlock()
	return h.passwordHash
}

// Enabled — true если admin password задан и можно register'ить routes.
func (h *Handler) Enabled() bool {
	return h.currentPasswordHash() != ""
}

// IsAuthenticated — проверяет что запрос имеет валидную admin session cookie.
// Используется из других packages (telemetry) для кросс-пакетного auth без
// circular import'а. NOT lenient — требует точного match IP + ttl ok.
func (h *Handler) IsAuthenticated(r *http.Request) bool {
	cookie, err := r.Cookie(sessionCookieName)
	if err != nil || cookie.Value == "" {
		return false
	}
	_, err = h.sessions.Validate(cookie.Value, h.extractIP(r))
	return err == nil
}

// Register — монтирует все /admin/* endpoints. Если !Enabled(), всё возвращает 404.
func (h *Handler) Register(mux *http.ServeMux) {
	// Все admin routes проходят через IP whitelist middleware.
	wrap := func(next http.HandlerFunc) http.Handler {
		return h.ipWhitelist.Middleware(
			h.securityHeaders(http.HandlerFunc(next)),
			h.extractIP,
		)
	}

	// Public routes (не требуют session):
	mux.Handle("/admin/login", wrap(h.loginPage))
	mux.Handle("/admin/api/login", wrap(h.apiLogin))

	// Protected — require session:
	mux.Handle("/admin", wrap(h.requireSession(h.dashboard)))
	mux.Handle("/admin/", wrap(h.requireSession(h.dashboard))) // trailing slash

	// API routes — require session + CSRF for mutations:
	mux.Handle("/admin/api/logout", wrap(h.requireCSRF(h.apiLogout)))
	mux.Handle("/admin/api/session", wrap(h.requireSession(h.apiSessionInfo)))
	mux.Handle("/admin/api/health", wrap(h.requireSession(h.apiHealth)))
	mux.Handle("/admin/api/version", wrap(h.requireCSRF(h.apiVersion)))
	mux.Handle("/admin/api/audit", wrap(h.requireSession(h.apiAudit)))
	mux.Handle("/admin/api/logs", wrap(h.requireSession(h.apiLogs)))
	mux.Handle("/admin/api/sessions", wrap(h.requireSession(h.apiSessions)))
	mux.Handle("/admin/api/ratelimit/reset", wrap(h.requireCSRF(h.apiResetRateLimit)))
	mux.Handle("/admin/api/sessions/logout-all", wrap(h.requireCSRF(h.apiLogoutAllOthers)))
	mux.Handle("/admin/api/password", wrap(h.requireCSRF(h.apiChangePassword)))
	mux.Handle("/admin/api/maintenance", wrap(h.requireCSRF(h.apiMaintenance)))
	mux.Handle("/admin/api/webrtc-sessions", wrap(h.requireSession(h.apiWebRTCSessions)))
	mux.Handle("/admin/api/webrtc-sessions/kill", wrap(h.requireCSRF(h.apiKillWebRTCSession)))
	mux.Handle("/admin/api/backup/db", wrap(h.requireSession(h.apiBackupDB)))
	mux.Handle("/admin/api/ban", wrap(h.requireCSRF(h.apiBanIP)))
	mux.Handle("/admin/api/ban/list", wrap(h.requireSession(h.apiListBans)))
	mux.Handle("/admin/api/suspicious-ips", wrap(h.requireSession(h.apiSuspiciousIPs)))

	// Hard-cut старый /dashboard — 410 Gone с подсказкой.
	mux.HandleFunc("/dashboard", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.WriteHeader(http.StatusGone)
		_, _ = w.Write([]byte(`<!doctype html><meta charset=utf-8><title>Gone</title>
<body style="font-family:sans-serif;padding:40px;background:#0f1117;color:#e1e4e8">
<h1>410 Gone</h1>
<p>Старый dashboard (/dashboard?key=...) отключён.</p>
<p>Новый admin panel: <a href="/admin/login" style="color:#58a6ff">/admin/login</a></p>
<p>Аутентификация теперь через login page с паролем и session cookie.</p>
</body>`))
	})

	// Periodic GC (каждые 5 мин) — удаляет expired sessions + rate-limit entries.
	go h.gcLoop()

	if h.Enabled() {
		h.logger.Log("INFO", "Admin", "admin_enabled",
			"admin panel enabled at /admin/login ip_whitelist="+boolStr(len(h.cfg.AllowedIPs) > 0),
			logging.Entry{})
	} else {
		h.logger.Log("INFO", "Admin", "admin_disabled",
			"admin panel disabled (no ADMIN_PASSWORD_HASH)",
			logging.Entry{})
	}
}

// ── HTML endpoints ─────────────────────────────────────────────────

func (h *Handler) loginPage(w http.ResponseWriter, r *http.Request) {
	if !h.Enabled() {
		http.Error(w, "admin not configured", http.StatusNotFound)
		return
	}
	// Уже залогинен? → redirect на dashboard.
	if cookie, err := r.Cookie(sessionCookieName); err == nil && cookie.Value != "" {
		if _, err := h.sessions.Validate(cookie.Value, h.extractIP(r)); err == nil {
			http.Redirect(w, r, "/admin", http.StatusFound)
			return
		}
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	_, _ = w.Write(loginHTML)
}

func (h *Handler) dashboard(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	_, _ = w.Write(dashboardHTML)
}

// ── API endpoints ──────────────────────────────────────────────────

type loginRequest struct {
	Password string `json:"password"`
}
type loginResponse struct {
	OK        bool   `json:"ok"`
	CSRFToken string `json:"csrf_token,omitempty"`
	Error     string `json:"error,omitempty"`
}

func (h *Handler) apiLogin(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.Header().Set("Allow", "POST")
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if !h.Enabled() {
		http.Error(w, "admin not configured", http.StatusNotFound)
		return
	}
	ip := h.extractIP(r)
	// Rate limit: 5 attempts per 15 min per IP.
	if !h.loginLimiter.Allow(ip) {
		h.audit.Log(Entry{IP: ip, Action: "login", Result: "denied", Reason: "rate_limited"})
		h.writeJSON(w, http.StatusTooManyRequests, loginResponse{
			Error: "Слишком много попыток. Подождите 15 минут или перезапустите сервер для сброса.",
		})
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, 1024)
	var req loginRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		h.writeJSON(w, http.StatusBadRequest, loginResponse{Error: "Некорректный запрос"})
		return
	}
	if !CheckPassword(req.Password, h.currentPasswordHash()) {
		h.audit.Log(Entry{IP: ip, Action: "login", Result: "fail", Reason: "wrong_password"})
		h.writeJSON(w, http.StatusUnauthorized, loginResponse{Error: "Неверный пароль"})
		return
	}
	// Success — reset rate limit for this IP, create session.
	h.loginLimiter.Reset(ip)
	sess, err := h.sessions.Create(ip)
	if err != nil {
		h.audit.Log(Entry{IP: ip, Action: "login", Result: "fail", Reason: "session_create_error"})
		h.writeJSON(w, http.StatusInternalServerError, loginResponse{Error: "Внутренняя ошибка сервера"})
		return
	}
	h.setSessionCookie(w, sess.ID)
	h.audit.Log(Entry{IP: ip, Action: "login", Result: "ok"})
	h.writeJSON(w, http.StatusOK, loginResponse{OK: true, CSRFToken: sess.CSRFToken})
}

func (h *Handler) apiLogout(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.Header().Set("Allow", "POST")
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	sess := SessionFromContext(r.Context())
	if sess != nil {
		h.sessions.Destroy(sess.ID)
		h.audit.Log(Entry{IP: h.extractIP(r), Action: "logout", Result: "ok"})
	}
	h.clearSessionCookie(w)
	h.writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// apiSessionInfo — возвращает CSRF token текущей сессии (UI нужно для форм).
// Safe: доступно только через cookie session, значит user уже аутентифицирован.
func (h *Handler) apiSessionInfo(w http.ResponseWriter, r *http.Request) {
	sess := SessionFromContext(r.Context())
	if sess == nil {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	h.writeJSON(w, http.StatusOK, map[string]any{
		"csrf_token":  sess.CSRFToken,
		"created_at":  sess.CreatedAt.Format(time.RFC3339),
		"last_active": sess.LastActive.Format(time.RFC3339),
		"idle_ttl":    int(h.cfg.SessionIdleTTL.Seconds()),
		"max_life":    int(h.cfg.SessionMaxLife.Seconds()),
	})
}

// apiHealth — server runtime stats (Phase 4).
type healthResponse struct {
	StartedAt       string `json:"started_at"`
	UptimeSeconds   int64  `json:"uptime_seconds"`
	Goroutines      int    `json:"goroutines"`
	HeapAllocBytes  uint64 `json:"heap_alloc_bytes"`
	SysBytes        uint64 `json:"sys_bytes"`
	ActiveSessions  int    `json:"active_sessions"`
	ActivePeers     int    `json:"active_peers"`
	ActiveRooms     int    `json:"active_rooms"`
	AdminSessions   int    `json:"admin_sessions"`
	GoVersion       string `json:"go_version"`
}

func (h *Handler) apiHealth(w http.ResponseWriter, r *http.Request) {
	var mem runtime.MemStats
	runtime.ReadMemStats(&mem)
	h.sessions.mu.RLock()
	adminSessCount := len(h.sessions.sessions)
	h.sessions.mu.RUnlock()

	var activeSessions, activePeers, activeRooms int
	if h.svc.Sessions != nil {
		activeSessions = h.svc.Sessions.ActiveCount()
	}
	if h.svc.Peers != nil {
		activePeers = h.svc.Peers.PeerCount()
		activeRooms = h.svc.Peers.RoomCount()
	}
	resp := healthResponse{
		StartedAt:      h.startedAt.Format(time.RFC3339),
		UptimeSeconds:  int64(time.Since(h.startedAt).Seconds()),
		Goroutines:     runtime.NumGoroutine(),
		HeapAllocBytes: mem.HeapAlloc,
		SysBytes:       mem.Sys,
		ActiveSessions: activeSessions,
		ActivePeers:    activePeers,
		ActiveRooms:    activeRooms,
		AdminSessions:  adminSessCount,
		GoVersion:      runtime.Version(),
	}
	h.writeJSON(w, http.StatusOK, resp)
}

// apiVersion — Client Version Manager (Phase 3).
// GET: current content of client-version.json
// POST: update client-version.json (all fields in body, overwrites).
type versionInfo struct {
	LatestVersion        string `json:"latest_version"`
	DownloadURL          string `json:"download_url"`
	ReleaseNotes         string `json:"release_notes"`
	MinCompatibleVersion string `json:"min_compatible_version"`
	PublishedAt          string `json:"published_at,omitempty"`
	SHA256               string `json:"sha256,omitempty"` // 64 hex chars — client verify downloaded file integrity
}

func (h *Handler) apiVersion(w http.ResponseWriter, r *http.Request) {
	if h.cfg.ClientVersionFile == "" {
		http.Error(w, "client version file not configured", http.StatusNotImplemented)
		return
	}
	switch r.Method {
	case http.MethodGet:
		h.apiVersionGet(w, r)
	case http.MethodPost, http.MethodPut:
		h.apiVersionPut(w, r)
	default:
		w.Header().Set("Allow", "GET, POST")
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

func (h *Handler) apiVersionGet(w http.ResponseWriter, r *http.Request) {
	raw, err := os.ReadFile(h.cfg.ClientVersionFile)
	if err != nil {
		if os.IsNotExist(err) {
			// Нет файла — возвращаем пустые поля + флаг, UI покажет "не задан".
			h.writeJSON(w, http.StatusOK, map[string]any{"exists": false})
			return
		}
		http.Error(w, "read error", http.StatusInternalServerError)
		return
	}
	var info versionInfo
	if err := json.Unmarshal(raw, &info); err != nil {
		http.Error(w, "parse error", http.StatusInternalServerError)
		return
	}
	h.writeJSON(w, http.StatusOK, map[string]any{
		"exists": true,
		"info":   info,
	})
}

func (h *Handler) apiVersionPut(w http.ResponseWriter, r *http.Request) {
	r.Body = http.MaxBytesReader(w, r.Body, 8*1024)
	var info versionInfo
	if err := json.NewDecoder(r.Body).Decode(&info); err != nil {
		http.Error(w, "invalid json", http.StatusBadRequest)
		return
	}
	if strings.TrimSpace(info.LatestVersion) == "" {
		http.Error(w, "latest_version is required", http.StatusBadRequest)
		return
	}
	if strings.TrimSpace(info.DownloadURL) == "" {
		http.Error(w, "download_url is required", http.StatusBadRequest)
		return
	}
	// Безопасность: download_url должен быть HTTPS (mitigate installer MitM).
	// Allow http:// ONLY если явно set ADMIN_ALLOW_HTTP_DOWNLOAD=1 (dev).
	if !strings.HasPrefix(info.DownloadURL, "https://") && os.Getenv("ADMIN_ALLOW_HTTP_DOWNLOAD") != "1" {
		http.Error(w, "download_url must be HTTPS (set ADMIN_ALLOW_HTTP_DOWNLOAD=1 to allow http)", http.StatusBadRequest)
		return
	}
	// SSRF protection: запрещаем URL на localhost / private IPs / link-local.
	// Admin не должен случайно/злонамеренно push'нуть http://127.0.0.1:8080/foo
	// который заставит пользовательский клиент скачивать что-то с локалки.
	if err := validateDownloadURL(info.DownloadURL); err != nil {
		http.Error(w, "invalid download_url: "+err.Error(), http.StatusBadRequest)
		return
	}
	// SHA256 optional, но если задан — должен быть 64 hex chars.
	if info.SHA256 != "" {
		info.SHA256 = strings.ToLower(strings.TrimSpace(info.SHA256))
		if !isHex64(info.SHA256) {
			http.Error(w, "sha256 must be 64 hex chars (lowercase)", http.StatusBadRequest)
			return
		}
	}
	// Записываем атомарно через temp file + rename.
	dir := h.cfg.ClientVersionFile + ".tmp"
	raw, err := json.MarshalIndent(info, "", "  ")
	if err != nil {
		http.Error(w, "marshal error", http.StatusInternalServerError)
		return
	}
	if err := os.WriteFile(dir, raw, 0o644); err != nil {
		http.Error(w, "write error: "+err.Error(), http.StatusInternalServerError)
		return
	}
	if err := os.Rename(dir, h.cfg.ClientVersionFile); err != nil {
		http.Error(w, "rename error: "+err.Error(), http.StatusInternalServerError)
		return
	}
	h.audit.Log(Entry{
		IP:     h.extractIP(r),
		Action: "update_client_version",
		Result: "ok",
		Metadata: map[string]string{
			"latest_version": info.LatestVersion,
			"download_url":   info.DownloadURL,
		},
	})
	h.writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// apiAudit — read last N entries from admin-audit.log (NDJSON).
// GET /admin/api/audit?limit=100
func (h *Handler) apiAudit(w http.ResponseWriter, r *http.Request) {
	if h.cfg.AuditLogPath == "" {
		h.writeJSON(w, http.StatusOK, map[string]any{"entries": []any{}, "note": "audit log not configured"})
		return
	}
	limit := 100
	if l := r.URL.Query().Get("limit"); l != "" {
		if parsed, err := strconv.Atoi(l); err == nil && parsed > 0 && parsed <= 1000 {
			limit = parsed
		}
	}
	raw, err := os.ReadFile(h.cfg.AuditLogPath)
	if err != nil {
		if os.IsNotExist(err) {
			h.writeJSON(w, http.StatusOK, map[string]any{"entries": []any{}, "note": "no entries yet"})
			return
		}
		http.Error(w, "read error", http.StatusInternalServerError)
		return
	}
	// Parse NDJSON — each line is separate entry. Keep last `limit` lines.
	lines := strings.Split(strings.TrimSpace(string(raw)), "\n")
	start := 0
	if len(lines) > limit {
		start = len(lines) - limit
	}
	entries := make([]map[string]any, 0, limit)
	for _, line := range lines[start:] {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		var e map[string]any
		if err := json.Unmarshal([]byte(line), &e); err == nil {
			entries = append(entries, e)
		}
	}
	// Reverse — newest first
	for i, j := 0, len(entries)-1; i < j; i, j = i+1, j-1 {
		entries[i], entries[j] = entries[j], entries[i]
	}
	h.writeJSON(w, http.StatusOK, map[string]any{
		"entries":    entries,
		"total_file": len(lines),
	})
}

// apiLogs — tail server logs (captured into /app/logs.log).
// GET /admin/api/logs?lines=100&level=WARN&category=websocket&search=193.168
//
// Categorization (2026-04-27 task B): фильтр по логическим группам, не сырому
// модулю, для quick troubleshooting:
//   - sessions  : API session_* (created/joined/closed/refresh/ttl_extended)
//   - websocket : WS peer_connected/disconnected/read_loop_exit + Signaling host_*
//   - auth      : Admin login_* + Auth token_* + UnattendedAuth probe/password
//   - security  : Session lockout_* + Admin ban_* + API rate_* + suspicious enum
//   - telemetry : Telemetry heartbeat / version_check / device_*
//   - issues    : level=WARN OR level=ERROR (любая категория, just severity filter)
//
// Free-text search через ?search= — case-insensitive substring match по всей
// raw строке (полезно для session_id/IP/peer_id lookup).
//
// Возвращает structured items с распарсенными ts/level/module/event/message
// для табличного отображения в UI вместо raw <pre>.
func (h *Handler) apiLogs(w http.ResponseWriter, r *http.Request) {
	lines := 100
	if l := r.URL.Query().Get("lines"); l != "" {
		if parsed, err := strconv.Atoi(l); err == nil && parsed > 0 && parsed <= 2000 {
			lines = parsed
		}
	}
	level := strings.ToUpper(r.URL.Query().Get("level"))
	category := strings.ToLower(r.URL.Query().Get("category"))
	search := r.URL.Query().Get("search")

	// Logger writes to "logs.log" relative to working dir. In Docker — /app/logs.log.
	raw, err := os.ReadFile("logs.log")
	if err != nil {
		if os.IsNotExist(err) {
			h.writeJSON(w, http.StatusOK, map[string]any{"items": []any{}, "note": "no logs"})
			return
		}
		http.Error(w, "read error: "+err.Error(), http.StatusInternalServerError)
		return
	}
	allLines := strings.Split(strings.TrimSpace(string(raw)), "\n")

	// Parse + filter в один проход. Структурированные items эффективнее raw lines
	// для UI (frontend не парсит JSON каждой строки сам).
	type logItem struct {
		Ts        string `json:"ts"`
		Level     string `json:"level"`
		Module    string `json:"module"`
		Event     string `json:"event"`
		Message   string `json:"message,omitempty"`
		SessionID string `json:"session_id,omitempty"`
		IP        string `json:"ip,omitempty"`
		Error     string `json:"error,omitempty"`
		Raw       string `json:"-"` // для search match (whole line)
	}
	var parsed []logItem
	for _, ln := range allLines {
		if strings.TrimSpace(ln) == "" {
			continue
		}
		var item logItem
		if jerr := json.Unmarshal([]byte(ln), &item); jerr != nil {
			// Non-JSON or malformed — keep с минимальным item для visibility.
			item = logItem{Message: ln}
		}
		item.Raw = ln
		parsed = append(parsed, item)
	}

	// Apply filters.
	filtered := make([]logItem, 0, len(parsed))
	for _, it := range parsed {
		if level != "" && it.Level != level {
			continue
		}
		if category != "" && !logMatchesCategory(it.Module, it.Event, it.Level, category) {
			continue
		}
		if search != "" && !strings.Contains(strings.ToLower(it.Raw), strings.ToLower(search)) {
			continue
		}
		filtered = append(filtered, it)
	}

	// Keep last N.
	start := 0
	if len(filtered) > lines {
		start = len(filtered) - lines
	}
	visible := filtered[start:]

	// Counts по категориям — для UI badge counters (small extra cost, всё уже parsed).
	counts := map[string]int{
		"sessions": 0, "websocket": 0, "auth": 0,
		"security": 0, "telemetry": 0, "issues": 0,
	}
	for _, it := range parsed {
		for cat := range counts {
			if logMatchesCategory(it.Module, it.Event, it.Level, cat) {
				counts[cat]++
			}
		}
	}

	h.writeJSON(w, http.StatusOK, map[string]any{
		"items":            visible,
		"total_filtered":   len(filtered),
		"total_all":        len(parsed),
		"category_counts":  counts,
		"category_filter":  category,
		"level_filter":     level,
		"search_filter":    search,
	})
}

// logMatchesCategory возвращает true если log entry попадает в указанную category.
// Categories логические — group'ируют по реальному use-case (не по module field).
func logMatchesCategory(module, event, level, category string) bool {
	switch category {
	case "sessions":
		return module == "API" && strings.HasPrefix(event, "session_")
	case "websocket":
		return module == "WS" || (module == "Signaling" && strings.HasPrefix(event, "host_"))
	case "auth":
		if module == "Admin" && (strings.HasPrefix(event, "login_") || strings.HasPrefix(event, "password_") || strings.HasPrefix(event, "logout")) {
			return true
		}
		if module == "Auth" {
			return true
		}
		if module == "UnattendedAuth" {
			return true
		}
		return false
	case "security":
		if module == "Session" && (strings.Contains(event, "lockout") || strings.Contains(event, "blocked")) {
			return true
		}
		if module == "Admin" && (strings.Contains(event, "ban") || strings.Contains(event, "ip_")) {
			return true
		}
		if module == "API" && strings.Contains(event, "rate") {
			return true
		}
		if module == "Suspicious" || strings.Contains(event, "suspicious") {
			return true
		}
		return false
	case "telemetry":
		return module == "Telemetry"
	case "issues":
		return level == "WARN" || level == "ERROR"
	}
	return false
}

// apiSessions — list active admin sessions (для мониторинга и force-logout).
// GET /admin/api/sessions
func (h *Handler) apiSessions(w http.ResponseWriter, r *http.Request) {
	self := SessionFromContext(r.Context())
	h.sessions.mu.RLock()
	defer h.sessions.mu.RUnlock()
	type sessInfo struct {
		ID         string `json:"id"`
		IP         string `json:"ip"`
		CreatedAt  string `json:"created_at"`
		LastActive string `json:"last_active"`
		IsMe       bool   `json:"is_me"`
	}
	list := make([]sessInfo, 0, len(h.sessions.sessions))
	for _, s := range h.sessions.sessions {
		// Не возвращаем полный ID — только prefix (чтобы можно было identify но не reuse)
		shortID := s.ID
		if len(shortID) > 8 {
			shortID = shortID[:8] + "..."
		}
		list = append(list, sessInfo{
			ID:         shortID,
			IP:         s.IP,
			CreatedAt:  s.CreatedAt.Format(time.RFC3339),
			LastActive: s.LastActive.Format(time.RFC3339),
			IsMe:       self != nil && s.ID == self.ID,
		})
	}
	h.writeJSON(w, http.StatusOK, map[string]any{
		"sessions": list,
		"count":    len(list),
	})
}

// apiResetRateLimit — очистить in-memory login rate limiter (admin action,
// требует CSRF). Полезно когда user'а заблокировало из-за опечаток.
// POST /admin/api/ratelimit/reset
func (h *Handler) apiResetRateLimit(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	h.loginLimiter.mu.Lock()
	count := len(h.loginLimiter.attempts)
	h.loginLimiter.attempts = make(map[string][]time.Time)
	h.loginLimiter.mu.Unlock()
	h.audit.Log(Entry{
		IP:     h.extractIP(r),
		Action: "reset_rate_limit",
		Result: "ok",
		Metadata: map[string]string{
			"cleared_ips": strconv.Itoa(count),
		},
	})
	h.writeJSON(w, http.StatusOK, map[string]any{"ok": true, "cleared_ips": count})
}

// apiLogoutAllOthers — kill все admin sessions КРОМЕ текущей (emergency
// feature — admin может выкинуть всех на случай утечки cookie).
// POST /admin/api/sessions/logout-all
func (h *Handler) apiLogoutAllOthers(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	self := SessionFromContext(r.Context())
	if self == nil {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	h.sessions.mu.Lock()
	count := 0
	for sid := range h.sessions.sessions {
		if sid != self.ID {
			delete(h.sessions.sessions, sid)
			count++
		}
	}
	h.sessions.mu.Unlock()
	h.audit.Log(Entry{
		IP:     h.extractIP(r),
		Action: "logout_all_others",
		Result: "ok",
		Metadata: map[string]string{
			"killed_sessions": strconv.Itoa(count),
		},
	})
	h.writeJSON(w, http.StatusOK, map[string]any{"ok": true, "killed_sessions": count})
}

// apiChangePassword — смена admin password через UI.
// POST /admin/api/password {"old":"...","new":"..."}
func (h *Handler) apiChangePassword(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, 2048)
	var req struct {
		Old string `json:"old"`
		New string `json:"new"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		h.writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Некорректный запрос"})
		return
	}
	if !CheckPassword(req.Old, h.currentPasswordHash()) {
		h.audit.Log(Entry{IP: h.extractIP(r), Action: "change_password", Result: "fail", Reason: "wrong_old"})
		h.writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "Старый пароль неверный"})
		return
	}
	if len(req.New) < 8 {
		h.writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Новый пароль слишком короткий (минимум 8 символов)"})
		return
	}
	newHash, err := HashPassword(req.New)
	if err != nil {
		h.writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "Ошибка генерации hash"})
		return
	}
	// Persist to file (если путь задан) чтобы пережить restart.
	if h.cfg.PasswordHashFile != "" {
		if dir := filepath.Dir(h.cfg.PasswordHashFile); dir != "" && dir != "." {
			_ = os.MkdirAll(dir, 0o700)
		}
		// Atomic write: temp + rename.
		tmp := h.cfg.PasswordHashFile + ".tmp"
		if err := os.WriteFile(tmp, []byte(newHash), 0o600); err != nil {
			h.writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "Не удалось записать файл: " + err.Error()})
			return
		}
		if err := os.Rename(tmp, h.cfg.PasswordHashFile); err != nil {
			h.writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "Не удалось переименовать: " + err.Error()})
			return
		}
	}
	// Update in-memory.
	h.passwordMu.Lock()
	h.passwordHash = newHash
	h.passwordMu.Unlock()
	h.audit.Log(Entry{IP: h.extractIP(r), Action: "change_password", Result: "ok"})
	h.writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// apiMaintenance — toggle/query maintenance mode.
// GET /admin/api/maintenance → текущее состояние
// POST {"enabled":true/false, "message":"..."} → изменить
func (h *Handler) apiMaintenance(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodGet:
		h.writeJSON(w, http.StatusOK, map[string]any{
			"enabled": h.maintenanceMode.Load(),
			"message": h.maintenanceMsg.Load(),
		})
	case http.MethodPost, http.MethodPut:
		r.Body = http.MaxBytesReader(w, r.Body, 2048)
		var req struct {
			Enabled bool   `json:"enabled"`
			Message string `json:"message"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			http.Error(w, "invalid json", http.StatusBadRequest)
			return
		}
		h.maintenanceMode.Store(req.Enabled)
		h.maintenanceMsg.Store(req.Message)
		h.audit.Log(Entry{
			IP: h.extractIP(r), Action: "maintenance_mode", Result: "ok",
			Metadata: map[string]string{"enabled": strconv.FormatBool(req.Enabled)},
		})
		h.writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

// IsMaintenance — public getter для других packages (session.Service видит это).
func (h *Handler) IsMaintenance() (bool, string) {
	enabled := h.maintenanceMode.Load()
	msg, _ := h.maintenanceMsg.Load().(string)
	return enabled, msg
}

// apiWebRTCSessions — list активных WebRTC sessions на сервере.
// GET /admin/api/webrtc-sessions
func (h *Handler) apiWebRTCSessions(w http.ResponseWriter, r *http.Request) {
	if h.svc.WebRTCSessions == nil {
		h.writeJSON(w, http.StatusOK, map[string]any{"sessions": []any{}, "note": "provider not configured"})
		return
	}
	list := h.svc.WebRTCSessions.ListActive()
	h.writeJSON(w, http.StatusOK, map[string]any{"sessions": list, "count": len(list)})
}

// apiKillWebRTCSession — force-close active session.
// POST /admin/api/webrtc-sessions/kill {"session_id":"..."}
func (h *Handler) apiKillWebRTCSession(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if h.svc.WebRTCKiller == nil {
		http.Error(w, "killer not configured", http.StatusNotImplemented)
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, 1024)
	var req struct {
		SessionID string `json:"session_id"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil || req.SessionID == "" {
		http.Error(w, "session_id required", http.StatusBadRequest)
		return
	}
	killed := h.svc.WebRTCKiller.KillSession(req.SessionID)
	h.audit.Log(Entry{
		IP: h.extractIP(r), Action: "kill_session",
		Result:   map[bool]string{true: "ok", false: "not_found"}[killed],
		Metadata: map[string]string{"session_id": req.SessionID},
	})
	h.writeJSON(w, http.StatusOK, map[string]bool{"ok": killed})
}

// apiBackupDB — download telemetry.db как файл.
// GET /admin/api/backup/db → binary download
// Использует http.ServeContent для автоматического:
//   - cleanup на client disconnect
//   - Range request support (resume)
//   - If-Modified-Since handling
func (h *Handler) apiBackupDB(w http.ResponseWriter, r *http.Request) {
	if h.cfg.TelemetryDBPath == "" {
		http.Error(w, "telemetry db path not configured", http.StatusNotImplemented)
		return
	}
	f, err := os.Open(h.cfg.TelemetryDBPath)
	if err != nil {
		http.Error(w, "open failed: "+err.Error(), http.StatusInternalServerError)
		return
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		http.Error(w, "stat failed", http.StatusInternalServerError)
		return
	}
	timestamp := time.Now().UTC().Format("2006-01-02_150405")
	filename := "zconect-telemetry-" + timestamp + ".db"
	w.Header().Set("Content-Type", "application/octet-stream")
	w.Header().Set("Content-Disposition", `attachment; filename="`+filename+`"`)
	h.audit.Log(Entry{IP: h.extractIP(r), Action: "download_db_backup", Result: "ok"})
	// http.ServeContent: streaming + proper cleanup + Range requests + ETag handling.
	// Не load'ит в память, корректно закрывает connections при disconnect.
	http.ServeContent(w, r, filename, info.ModTime(), f)
}

// ── IP ban list (in-memory) ────────────────────────────────────────

var (
	bannedIPsMu sync.RWMutex
	bannedIPs   = make(map[string]BanEntry)
)

type BanEntry struct {
	IP        string    `json:"ip"`
	Reason    string    `json:"reason"`
	CreatedAt time.Time `json:"created_at"`
	// ExpiresAt — если zero, бан постоянный (ручной admin ban).
	// Non-zero → auto-expire при IsIPBanned lookup после этого времени.
	// Используется для auto-ban by bruteforce tracker (default 1 час).
	ExpiresAt time.Time `json:"expires_at,omitempty"`
	// Auto — true для банов, поставленных автоматически (brute-force detector).
	// Админ видит отметку и понимает что это не его ручная запись.
	Auto bool `json:"auto,omitempty"`
}

// IsIPBanned — public check для других packages (session API, signaling).
// Auto-expires банов с ExpiresAt в прошлом (lazy cleanup — не требует background sweeper
// для правильного поведения, но optional sweeper в runSweeper() не даёт мапе расти).
func IsIPBanned(ip string) (bool, string) {
	bannedIPsMu.RLock()
	entry, ok := bannedIPs[ip]
	bannedIPsMu.RUnlock()
	if !ok {
		return false, ""
	}
	if !entry.ExpiresAt.IsZero() && time.Now().UTC().After(entry.ExpiresAt) {
		// Expired — cleanup + treat as not-banned
		bannedIPsMu.Lock()
		// Re-check под write lock чтобы избежать race c concurrent AutoBan
		if cur, ok := bannedIPs[ip]; ok && !cur.ExpiresAt.IsZero() && time.Now().UTC().After(cur.ExpiresAt) {
			delete(bannedIPs, ip)
		}
		bannedIPsMu.Unlock()
		return false, ""
	}
	return true, entry.Reason
}

// AutoBanIP — автоматический ban из brute-force detector'а. В отличие от ручного
// apiBanIP задаёт ExpiresAt и Auto=true. Idempotent: повторный вызов для уже
// banned IP просто продлевает ExpiresAt и сохраняет reason (или overrides
// если reason поменялся).
func AutoBanIP(ip, reason string, duration time.Duration) {
	if ip == "" {
		return
	}
	now := time.Now().UTC()
	bannedIPsMu.Lock()
	defer bannedIPsMu.Unlock()
	bannedIPs[ip] = BanEntry{
		IP:        ip,
		Reason:    sanitizeReason(reason),
		CreatedAt: now,
		ExpiresAt: now.Add(duration),
		Auto:      true,
	}
}

// bannedSweeper — periodic cleanup expired auto-bans. Не критичен для
// correctness (IsIPBanned сам expires-check делает), но не даёт мапе расти
// unbounded при большом потоке auto-банов.
func runBannedSweeper(interval time.Duration) {
	ticker := time.NewTicker(interval)
	go func() {
		for range ticker.C {
			now := time.Now().UTC()
			bannedIPsMu.Lock()
			for ip, e := range bannedIPs {
				if !e.ExpiresAt.IsZero() && now.After(e.ExpiresAt) {
					delete(bannedIPs, ip)
				}
			}
			bannedIPsMu.Unlock()
		}
	}()
}

// StartBannedSweeper — public hook для cmd/signaling запуска sweeper'а.
func StartBannedSweeper() {
	runBannedSweeper(time.Minute)
}

// apiBanIP — add IP to blocklist OR remove it.
// POST /admin/api/ban {"ip":"1.2.3.4","reason":"spam","action":"add"|"remove"}
func (h *Handler) apiBanIP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, 1024)
	var req struct {
		IP     string `json:"ip"`
		Reason string `json:"reason"`
		Action string `json:"action"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, "invalid json", http.StatusBadRequest)
		return
	}
	if req.IP == "" {
		http.Error(w, "ip required", http.StatusBadRequest)
		return
	}
	ip := strings.TrimSpace(req.IP)
	// Validate IP — реальный IP формат, не произвольная строка
	if net.ParseIP(ip) == nil {
		http.Error(w, "invalid IP format", http.StatusBadRequest)
		return
	}
	action := strings.ToLower(strings.TrimSpace(req.Action))
	if action != "add" && action != "remove" {
		action = "add"
	}
	// Sanitize reason: truncate + strip control chars (newlines могли бы injection'уть
	// в audit log NDJSON). Max 200 chars acceptable для short reason.
	reason := sanitizeReason(req.Reason)

	bannedIPsMu.Lock()
	switch action {
	case "add":
		bannedIPs[ip] = BanEntry{IP: ip, Reason: reason, CreatedAt: time.Now().UTC()}
	case "remove":
		delete(bannedIPs, ip)
	}
	count := len(bannedIPs)
	bannedIPsMu.Unlock()

	h.audit.Log(Entry{
		IP: h.extractIP(r), Action: "ban_" + action, Result: "ok",
		Metadata: map[string]string{"target_ip": ip, "reason": reason},
	})
	h.writeJSON(w, http.StatusOK, map[string]any{"ok": true, "total_bans": count})
}

// sanitizeReason — truncate + strip control chars / newlines.
// Защищает NDJSON audit log от injection'а псевдо-entries через reason поле.
func sanitizeReason(s string) string {
	s = strings.TrimSpace(s)
	if len(s) > 200 {
		s = s[:200]
	}
	// Replace любые control chars (включая \n \r \t) на space.
	var b strings.Builder
	b.Grow(len(s))
	for _, r := range s {
		if r < 0x20 || r == 0x7F {
			b.WriteByte(' ')
		} else {
			b.WriteRune(r)
		}
	}
	return b.String()
}

// apiListBans — GET /admin/api/ban/list
func (h *Handler) apiListBans(w http.ResponseWriter, r *http.Request) {
	bannedIPsMu.RLock()
	list := make([]BanEntry, 0, len(bannedIPs))
	for _, e := range bannedIPs {
		list = append(list, e)
	}
	bannedIPsMu.RUnlock()
	h.writeJSON(w, http.StatusOK, map[string]any{"bans": list, "count": len(list)})
}

// apiSuspiciousIPs — GET /admin/api/suspicious-ips?min=5
// Returns list IPs с >=min неуспешных /join попыток в sliding window tracker'а.
// Если SuspiciousIPs provider не инжектнут (env var отключил tracker) — пустой list.
// Window length (секунды) возвращается в window_sec для UI контекста "за последние N сек".
func (h *Handler) apiSuspiciousIPs(w http.ResponseWriter, r *http.Request) {
	if h.svc.SuspiciousIPs == nil {
		h.writeJSON(w, http.StatusOK, map[string]any{
			"ips": []SuspiciousIP{}, "count": 0, "window_sec": 0,
			"note": "tracker not configured",
		})
		return
	}
	minCount := 1
	if m := r.URL.Query().Get("min"); m != "" {
		if parsed, err := strconv.Atoi(m); err == nil && parsed > 0 && parsed <= 1000 {
			minCount = parsed
		}
	}
	ips := h.svc.SuspiciousIPs.Snapshot(minCount)
	h.writeJSON(w, http.StatusOK, map[string]any{
		"ips":        ips,
		"count":      len(ips),
		"window_sec": h.svc.SuspiciousIPs.WindowSec(),
	})
}

// isHex64 — true если s длиной 64 и все chars — hex lowercase.
func isHex64(s string) bool {
	if len(s) != 64 {
		return false
	}
	for _, c := range s {
		if !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) {
			return false
		}
	}
	return true
}

// validateDownloadURL — SSRF protection + sanity check.
// Запрещает localhost, private IPv4 (10/8, 172.16/12, 192.168/16), link-local,
// и scheme-only URL без host. Разрешает HTTPS + HTTP (если env allow).
func validateDownloadURL(raw string) error {
	u, err := neturl.Parse(raw)
	if err != nil {
		return fmt.Errorf("parse: %w", err)
	}
	if u.Scheme != "https" && u.Scheme != "http" {
		return fmt.Errorf("scheme must be http/https (got %q)", u.Scheme)
	}
	host := u.Hostname()
	if host == "" {
		return fmt.Errorf("host required")
	}
	// Reject localhost hostnames explicitly.
	lower := strings.ToLower(host)
	if lower == "localhost" || lower == "localhost.localdomain" ||
		strings.HasSuffix(lower, ".localhost") || strings.HasSuffix(lower, ".local") {
		return fmt.Errorf("localhost disallowed")
	}
	// If host parses to IP, check against private ranges.
	if ip := net.ParseIP(host); ip != nil {
		if ip.IsLoopback() || ip.IsPrivate() || ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast() || ip.IsUnspecified() {
			return fmt.Errorf("private/loopback IP disallowed")
		}
	}
	return nil
}

// ── Helpers ────────────────────────────────────────────────────────

// Cookie — Secure (если prod), HttpOnly, SameSite=Strict, Path=/admin.
func (h *Handler) setSessionCookie(w http.ResponseWriter, sid string) {
	http.SetCookie(w, &http.Cookie{
		Name:     sessionCookieName,
		Value:    sid,
		// Path=/ — cookie ходит и на /admin/*, и на /api/v1/telemetry/stats
		// (dashboard UI fetch'ит stats напрямую). Если бы Path=/admin, браузер
		// не отсылал бы cookie на /api/v1/... → 401 Unauthorized.
		// HttpOnly + SameSite=Lax + IP binding всё равно защищают.
		Path:     "/",
		HttpOnly: true,
		Secure:   h.cfg.CookieSecure,
		// Lax вместо Strict: Strict на HTTP (без HTTPS) иногда блокируется
		// браузером после location.replace() от same-origin. Lax разрешает
		// top-level navigation (GET) — достаточно безопасно для login flow,
		// и все mutating endpoints защищены CSRF token'ом.
		SameSite: http.SameSiteLaxMode,
		MaxAge:   int(h.cfg.SessionMaxLife.Seconds()),
	})
}

func (h *Handler) clearSessionCookie(w http.ResponseWriter) {
	http.SetCookie(w, &http.Cookie{
		Name:     sessionCookieName,
		Value:    "",
		// Path=/ — cookie ходит и на /admin/*, и на /api/v1/telemetry/stats
		// (dashboard UI fetch'ит stats напрямую). Если бы Path=/admin, браузер
		// не отсылал бы cookie на /api/v1/... → 401 Unauthorized.
		// HttpOnly + SameSite=Lax + IP binding всё равно защищают.
		Path:     "/",
		HttpOnly: true,
		Secure:   h.cfg.CookieSecure,
		// Lax вместо Strict: Strict на HTTP (без HTTPS) иногда блокируется
		// браузером после location.replace() от same-origin. Lax разрешает
		// top-level navigation (GET) — достаточно безопасно для login flow,
		// и все mutating endpoints защищены CSRF token'ом.
		SameSite: http.SameSiteLaxMode,
		MaxAge:   -1,
	})
}

// securityHeaders — HSTS + CSP + X-Content-Type-Options + X-Frame-Options.
// Применяется ко всем admin routes.
func (h *Handler) securityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		wh := w.Header()
		// HSTS: force HTTPS в browser на 1 год. Применяется ТОЛЬКО если Secure cookie mode.
		// (в dev/http могли бы spamm'ить HSTS → прод бы отказался).
		if h.cfg.CookieSecure {
			wh.Set("Strict-Transport-Security", "max-age=31536000; includeSubDomains")
		}
		// CSP: login.html и dashboard.html используют inline <script> для JS логики
		// (иначе нужен отдельный endpoint для .js файла). 'unsafe-inline' acceptable:
		// весь HTML — server-rendered template без user input, XSS vector отсутствует.
		wh.Set("Content-Security-Policy",
			"default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'")
		// Block MIME sniffing
		wh.Set("X-Content-Type-Options", "nosniff")
		// Block iframe embedding
		wh.Set("X-Frame-Options", "DENY")
		// Minimal referrer info to 3rd parties
		wh.Set("Referrer-Policy", "same-origin")
		next.ServeHTTP(w, r)
	})
}

func (h *Handler) writeJSON(w http.ResponseWriter, status int, body any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(body)
}

// extractIP — trustedProxies aware (supports CIDR + X-Real-IP + X-Forwarded-For).
// Delegates to shared realip.Extractor. См. server/internal/realip/extractor.go.
func (h *Handler) extractIP(r *http.Request) string {
	return h.realIP.Extract(r)
}

func (h *Handler) gcLoop() {
	ticker := time.NewTicker(5 * time.Minute)
	defer ticker.Stop()
	for range ticker.C {
		if atomic.LoadInt32(&shutdownFlag) == 1 {
			return
		}
		h.sessions.Gc()
		h.loginLimiter.Gc()
	}
}

// Shutdown — graceful cleanup (call from main on SIGTERM).
func (h *Handler) Shutdown() {
	atomic.StoreInt32(&shutdownFlag, 1)
	if h.audit != nil {
		_ = h.audit.Close()
	}
}

var shutdownFlag int32

func boolStr(b bool) string {
	if b {
		return "yes"
	}
	return "no"
}
