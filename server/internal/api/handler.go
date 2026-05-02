package api

import (
	"encoding/json"
	"errors"
	"net"
	"net/http"
	"strconv"
	"strings"
	"time"

	"zconect/server/internal/auth"
	"zconect/server/internal/logging"
	"zconect/server/internal/realip"
	"zconect/server/internal/session"
	"zconect/server/internal/turn"
)

type Handler struct {
	sessions *session.Service
	logger   *logging.Logger
	tokens   *auth.TokenService

	// startedAt — момент создания handler'а. Используется /api/v1/status для
	// reporting uptime_seconds без дополнительной инфраструктуры.
	startedAt time.Time

	// realIP — extractor с поддержкой X-Forwarded-For от trusted proxies (Caddy в docker).
	// Без этого в audit log / admin panel все client IPs становятся Caddy IP (172.18.0.4)
	// когда signaling сидит за Caddy reverse proxy. SetTrustedProxies injects из main.go.
	realIP *realip.Extractor

	// TURN rotating credentials — если turnSecret пуст, клиенту не возвращается
	// turn_servers (клиент использует static config из своих settings — legacy mode).
	turnSecret     string
	turnTTL        time.Duration
	turnPublicAddr string // "host:port" для client ICE config (e.g. "92.63.102.244:3478")
	turnRealm      string

	// Inject'ятся из main.go после создания admin handler (избегаем import cycle).
	maintenanceCheck func() (enabled bool, message string)
	bannedIPCheck    func(ip string) (banned bool, reason string)
	// recordJoinFailure — callback для per-IP enumeration tracker (observability).
	// Если nil — tracking отключён. Инжектится из main.go.
	recordJoinFailure func(ip string)
}

func NewHandler(sessions *session.Service, logger *logging.Logger, tokens *auth.TokenService) *Handler {
	return &Handler{
		sessions:  sessions,
		logger:    logger,
		tokens:    tokens,
		startedAt: time.Now(),
		realIP:    realip.New(nil), // default: no trusted proxies (direct mode)
	}
}

// SetTrustedProxies — inject'им CIDRs/IPs trusted to set X-Forwarded-For.
// Должно вызываться из main.go ПОСЛЕ Load'а config'а. Параметры:
//   - TRUSTED_PROXIES в .env (например "172.16.0.0/12" для Docker bridge)
func (h *Handler) SetTrustedProxies(trustedProxies []string) {
	h.realIP = realip.New(trustedProxies)
}

// SetTurnAuth — inject'им TURN rotating config после Load(). Если secret пустой,
// клиенту не возвращается turn_servers (клиент falls back на static config).
func (h *Handler) SetTurnAuth(secret string, ttl time.Duration, publicAddr, realm string) {
	h.turnSecret = secret
	h.turnTTL = ttl
	h.turnPublicAddr = publicAddr
	h.turnRealm = realm
}

// TurnServerConfig — one entry в turn_servers array response'а. Структура
// совпадает с RTCIceServer (клиент может напрямую передать в ICE config).
type TurnServerConfig struct {
	// URLs — список STUN/TURN URLs. Обычно один TURN URL с разными transport options.
	URLs []string `json:"urls"`
	// Username — для TURN auth. Формат "<exp_ts>:<session_id>" (см. turn.Generate).
	Username string `json:"username,omitempty"`
	// Credential — HMAC-подписанный token. Пустой для public STUN.
	Credential string `json:"credential,omitempty"`
	// ExpiresAtUnix — когда credential перестанет работать. Клиент должен сделать
	// session refresh до этого момента для нового credential.
	ExpiresAtUnix int64 `json:"expires_at_unix,omitempty"`
	// TTLSeconds — convenience, = ExpiresAtUnix - now().
	TTLSeconds int `json:"ttl_seconds,omitempty"`
}

// generateTurnServers — хелпер для session create/join/refresh. Возвращает nil
// если rotating TURN отключён (turnSecret empty или turnPublicAddr empty).
// Клиент тогда использует static TurnUrl/Username/Password из своих settings.
func (h *Handler) generateTurnServers(sessionID string) []TurnServerConfig {
	if h.turnSecret == "" || h.turnPublicAddr == "" {
		return nil
	}
	cred := turn.Generate(h.turnSecret, sessionID, h.turnTTL)
	if cred.Username == "" {
		return nil
	}
	// Один TURN server с UDP transport. При желании клиент сам может создать
	// multiple entries с разными transports (tcp, tls) из того же URL.
	return []TurnServerConfig{{
		URLs: []string{
			"turn:" + h.turnPublicAddr + "?transport=udp",
		},
		Username:      cred.Username,
		Credential:    cred.Credential,
		ExpiresAtUnix: cred.ExpiresAtUnix,
		TTLSeconds:    cred.TTLSeconds,
	}}
}

// SetMaintenanceCheck — inject'им после admin init.
func (h *Handler) SetMaintenanceCheck(f func() (bool, string)) { h.maintenanceCheck = f }

// SetBannedIPCheck — inject'им после admin init.
func (h *Handler) SetBannedIPCheck(f func(string) (bool, string)) { h.bannedIPCheck = f }

// SetJoinFailureRecorder — inject callback'а bruteforce.IPTracker для per-IP
// enumeration detection. Вызывается из joinSession при каждом неуспешном attempt
// (любая ошибка — invalid login_code, wrong pass, locked, blocked).
func (h *Handler) SetJoinFailureRecorder(f func(string)) { h.recordJoinFailure = f }

// maxBodySize limits request body to 4 KB — all API payloads are small JSON.
const maxBodySize = 4 * 1024

func (h *Handler) Register(mux *http.ServeMux) {
	mux.HandleFunc("/healthz", h.health)
	mux.HandleFunc("/api/v1/status", h.status)
	mux.HandleFunc("/api/v1/session/create", h.createSession)
	mux.HandleFunc("/api/v1/session/join", h.joinSession)
	mux.HandleFunc("/api/v1/session/close", h.closeSession)
	mux.HandleFunc("/api/v1/session/refresh", h.refreshSession)
	mux.HandleFunc("/api/v1/presence", h.presence)
}

// presence — bulk online check for Address Book contacts.
// GET /api/v1/presence?logins=12345678,87654321,...
// Returns: {"online": {"12345678": true, "87654321": false}}
// Это light endpoint — O(N) map lookup, no DB hit. Rate limit handles абьюз.
func (h *Handler) presence(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		writeJSON(w, http.StatusMethodNotAllowed, map[string]string{"error": "method not allowed"})
		return
	}
	raw := r.URL.Query().Get("logins")
	if raw == "" {
		writeJSON(w, http.StatusOK, map[string]any{"online": map[string]bool{}})
		return
	}
	// Parse CSV, dedupe, limit 100 logins чтобы не abuse (response size + CPU).
	parts := strings.Split(raw, ",")
	if len(parts) > 100 {
		parts = parts[:100]
	}
	seen := make(map[string]bool, len(parts))
	logins := make([]string, 0, len(parts))
	for _, p := range parts {
		p = strings.TrimSpace(p)
		// Validate: must be 8 digits (login code format)
		if len(p) != 8 {
			continue
		}
		allDigit := true
		for _, c := range p {
			if c < '0' || c > '9' {
				allDigit = false
				break
			}
		}
		if !allDigit {
			continue
		}
		if seen[p] {
			continue
		}
		seen[p] = true
		logins = append(logins, p)
	}
	result := h.sessions.CheckLoginsOnline(logins)
	writeJSON(w, http.StatusOK, map[string]any{"online": result})
}

type createSessionRequest struct {
	RequestUnattended bool   `json:"request_unattended"`
	ExpiresInSec      int    `json:"expires_in_sec,omitempty"`
	MachineID         string `json:"machine_id,omitempty"`
	DeviceSecret      string `json:"device_secret,omitempty"` // NET-03: proof of device ownership
}

type createSessionResponse struct {
	SessionID    string             `json:"session_id"`
	LoginCode    string             `json:"login_code"`
	PassCode     string             `json:"pass_code"`
	ExpiresInSec int                `json:"expires_in_sec"`
	WSURL        string             `json:"ws_url"`
	WSToken      string             `json:"ws_token"`
	OwnerSecret  string             `json:"owner_secret"`             // F-03: returned only to host for close/refresh auth
	DeviceSecret string             `json:"device_secret,omitempty"`  // NET-03: returned for machine binding
	TurnServers  []TurnServerConfig `json:"turn_servers,omitempty"`   // Rotating creds; nil = client fallback на static config
}

func (h *Handler) createSession(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeJSON(w, http.StatusMethodNotAllowed, map[string]string{"error": "method not allowed"})
		return
	}

	// Maintenance mode — admin may toggle это чтобы пауза перед redeploy.
	if h.maintenanceCheck != nil {
		if enabled, msg := h.maintenanceCheck(); enabled {
			if msg == "" {
				msg = "Server is under maintenance. Try again later."
			}
			writeJSON(w, http.StatusServiceUnavailable, map[string]string{"error": msg})
			return
		}
	}

	// Ban check — заблокированные IP получают 403.
	if h.bannedIPCheck != nil {
		if banned, reason := h.bannedIPCheck(h.realIP.Extract(r)); banned {
			h.logger.Log("WARN", "API", "banned_ip_blocked", "banned IP blocked from create",
				logging.Entry{IP: h.realIP.Extract(r), Error: reason})
			writeJSON(w, http.StatusForbidden, map[string]string{"error": "banned"})
			return
		}
	}

	var req createSessionRequest
	r.Body = http.MaxBytesReader(w, r.Body, maxBodySize)
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid json"})
		return
	}

	sess, err := h.sessions.CreateWithOpts(session.CreateOpts{
		RequireConfirm: !req.RequestUnattended,
		ExpiresInSec:   req.ExpiresInSec,
		MachineID:      req.MachineID,
		DeviceSecret:   req.DeviceSecret,
	})
	if err != nil {
		h.logger.Log("ERROR", "API", "session_create_failed", "failed to create session", logging.Entry{Error: err.Error(), IP: h.realIP.Extract(r)})
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "internal error"})
		return
	}

	expiresIn := int(time.Until(sess.ExpiresAt).Seconds())
	if expiresIn < 0 {
		expiresIn = 0
	}

	h.logger.Log("INFO", "API", "session_created", "session created", logging.Entry{SessionID: sess.ID, IP: h.realIP.Extract(r)})
	writeJSON(w, http.StatusOK, createSessionResponse{
		SessionID:    sess.ID,
		LoginCode:    sess.LoginCode,
		PassCode:     sess.PassCode,
		ExpiresInSec: expiresIn,
		WSURL:        "/ws",
		WSToken:      h.tokens.Generate(sess.ID),
		OwnerSecret:  sess.OwnerSecret,
		DeviceSecret: sess.DeviceSecret,
		TurnServers:  h.generateTurnServers(sess.ID),
	})
}

type joinSessionRequest struct {
	LoginCode string `json:"login_code"`
	PassCode  string `json:"pass_code"`
}

type joinSessionResponse struct {
	SessionID      string             `json:"session_id"`
	RequireConfirm bool               `json:"require_confirm"`
	State          string             `json:"state"`
	WSURL          string             `json:"ws_url"`
	WSToken        string             `json:"ws_token"`
	TurnServers    []TurnServerConfig `json:"turn_servers,omitempty"` // Rotating creds (mirrors createSession)
}

func (h *Handler) joinSession(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeJSON(w, http.StatusMethodNotAllowed, map[string]string{"error": "method not allowed"})
		return
	}

	// Maintenance mode — bugfix audit 2026-04-24 (H-1): раньше maintenance
	// гейтил только create, а join/refresh пропускал → существующие клиенты
	// продолжали подключаться во время "maintenance" окна. Для полной quiesce
	// перед redeploy нужно блокировать и join тоже.
	if h.maintenanceCheck != nil {
		if enabled, msg := h.maintenanceCheck(); enabled {
			if msg == "" {
				msg = "Server is under maintenance. Try again later."
			}
			writeJSON(w, http.StatusServiceUnavailable, map[string]string{"error": msg})
			return
		}
	}

	// Ban check — заблокированные IP не могут join'иться.
	if h.bannedIPCheck != nil {
		if banned, _ := h.bannedIPCheck(h.realIP.Extract(r)); banned {
			writeJSON(w, http.StatusForbidden, map[string]string{"error": "banned"})
			return
		}
	}

	var req joinSessionRequest
	r.Body = http.MaxBytesReader(w, r.Body, maxBodySize)
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid json"})
		return
	}
	if len(req.LoginCode) != 8 || len(req.PassCode) != 8 {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "login_code and pass_code must be 8 digits"})
		return
	}

	sess, err := h.sessions.Join(req.LoginCode, req.PassCode)
	if err != nil {
		// NET-02 fix: unified error response to prevent login code enumeration.
		// Only distinguish lock (429) и blocked (423 Locked) — остальное generic 401.
		status := http.StatusUnauthorized
		msg := "invalid credentials"
		var retryAfterSec int
		if errors.Is(err, session.ErrLocked) {
			status = http.StatusTooManyRequests
			msg = "session locked"
			// Compute Retry-After для UI UX — клиент показывает countdown user'у вместо
			// generic "too many attempts". Round up чтобы не показать 0 сек на самом краю.
			remaining := h.sessions.LockRemaining(req.LoginCode)
			if remaining > 0 {
				retryAfterSec = int(remaining.Seconds()) + 1
			}
		} else if errors.Is(err, session.ErrBlocked) {
			// 423 Locked (WebDAV, но подходит семантически: resource locked до admin action)
			status = http.StatusLocked
			msg = "session blocked"
		}
		ip := h.realIP.Extract(r)
		h.logger.Log("WARN", "API", "session_join_failed", msg, logging.Entry{Error: err.Error(), IP: ip})
		// Per-IP enumeration tracking — считаем ANY failure от одного IP.
		// Sess-level counter не инкрементируется на invalid login_code, поэтому
		// per-IP — единственный способ увидеть enumeration attack (attacker перебирает
		// 00000000, 11111111, ... ищет valid login).
		if h.recordJoinFailure != nil {
			h.recordJoinFailure(ip)
		}
		if retryAfterSec > 0 {
			// RFC 7231: Retry-After — seconds integer (без unit'а) или HTTP-date.
			w.Header().Set("Retry-After", strconv.Itoa(retryAfterSec))
		}
		// Клиент получает и error string, и retry_after в body — не обязан парсить header.
		body := map[string]any{"error": msg}
		if retryAfterSec > 0 {
			body["retry_after_sec"] = retryAfterSec
		}
		writeJSON(w, status, body)
		return
	}

	h.logger.Log("INFO", "API", "session_joined", "session joined", logging.Entry{SessionID: sess.ID, IP: h.realIP.Extract(r)})
	writeJSON(w, http.StatusOK, joinSessionResponse{
		SessionID:      sess.ID,
		RequireConfirm: sess.RequireConfirm,
		State:          string(sess.State),
		WSURL:          "/ws",
		WSToken:        h.tokens.Generate(sess.ID),
		TurnServers:    h.generateTurnServers(sess.ID),
	})
}

type closeSessionRequest struct {
	SessionID   string `json:"session_id"`
	OwnerSecret string `json:"owner_secret"`
}

func (h *Handler) closeSession(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeJSON(w, http.StatusMethodNotAllowed, map[string]string{"error": "method not allowed"})
		return
	}

	var req closeSessionRequest
	r.Body = http.MaxBytesReader(w, r.Body, maxBodySize)
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid json"})
		return
	}
	if req.SessionID == "" {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "session_id is required"})
		return
	}

	if err := h.sessions.Close(req.SessionID, req.OwnerSecret); err != nil {
		status := http.StatusNotFound
		msg := "session not found"
		if errors.Is(err, session.ErrForbidden) {
			status = http.StatusForbidden
			msg = "forbidden"
		}
		h.logger.Log("WARN", "API", "session_close_failed", "session close failed", logging.Entry{Error: err.Error(), IP: h.realIP.Extract(r)})
		writeJSON(w, status, map[string]string{"error": msg})
		return
	}

	h.logger.Log("INFO", "API", "session_closed", "session closed", logging.Entry{SessionID: req.SessionID, IP: h.realIP.Extract(r)})
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

type refreshSessionRequest struct {
	SessionID      string `json:"session_id"`
	OwnerSecret    string `json:"owner_secret"`
	ExpiresInSec   int    `json:"expires_in_sec,omitempty"`
	RegeneratePass bool   `json:"regenerate_pass,omitempty"` // true = change pass_code (manual refresh); false = keep existing (reconnect)
}

type refreshSessionResponse struct {
	SessionID    string             `json:"session_id"`
	LoginCode    string             `json:"login_code"`
	PassCode     string             `json:"pass_code"`
	ExpiresInSec int                `json:"expires_in_sec"`
	WSToken      string             `json:"ws_token"`
	TurnServers  []TurnServerConfig `json:"turn_servers,omitempty"` // Rotating creds — refresh обязан вернуть свежие
}

func (h *Handler) refreshSession(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeJSON(w, http.StatusMethodNotAllowed, map[string]string{"error": "method not allowed"})
		return
	}

	// Maintenance mode — audit H-1 fix 2026-04-24: также блокируем refresh
	// чтобы existing sessions не продлевались во время redeploy window.
	if h.maintenanceCheck != nil {
		if enabled, msg := h.maintenanceCheck(); enabled {
			if msg == "" {
				msg = "Server is under maintenance. Try again later."
			}
			writeJSON(w, http.StatusServiceUnavailable, map[string]string{"error": msg})
			return
		}
	}

	var req refreshSessionRequest
	r.Body = http.MaxBytesReader(w, r.Body, maxBodySize)
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid json"})
		return
	}
	if req.SessionID == "" {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "session_id is required"})
		return
	}

	sess, err := h.sessions.Refresh(req.SessionID, req.OwnerSecret, req.ExpiresInSec, req.RegeneratePass)
	if err != nil {
		status := http.StatusNotFound
		msg := "session not found"
		if errors.Is(err, session.ErrExpired) {
			status = http.StatusGone
			msg = "session expired"
		} else if errors.Is(err, session.ErrForbidden) {
			status = http.StatusForbidden
			msg = "forbidden"
		}
		h.logger.Log("WARN", "API", "session_refresh_failed", msg, logging.Entry{Error: err.Error(), SessionID: req.SessionID, IP: h.realIP.Extract(r)})
		writeJSON(w, status, map[string]string{"error": msg})
		return
	}

	expiresIn := int(time.Until(sess.ExpiresAt).Seconds())
	if expiresIn < 0 {
		expiresIn = 0
	}

	// Различаем два разных refresh-режима в логах чтобы admin не путался:
	//   - regenerate_pass=true → pass_code поменялся (security-relevant, INFO)
	//   - regenerate_pass=false → просто TTL extended (WS reconnect keepalive, DEBUG)
	// Раньше оба писали "session password refreshed" что сбивало с толку:
	// видно было много INFO events хотя пароль на самом деле менялся только в части из них.
	if req.RegeneratePass {
		h.logger.Log("INFO", "API", "session_pass_regenerated", "session pass_code regenerated + TTL extended",
			logging.Entry{SessionID: sess.ID, IP: h.realIP.Extract(r)})
	} else {
		h.logger.Log("DEBUG", "API", "session_ttl_extended", "session TTL extended (pass unchanged)",
			logging.Entry{SessionID: sess.ID, IP: h.realIP.Extract(r)})
	}
	writeJSON(w, http.StatusOK, refreshSessionResponse{
		SessionID:    sess.ID,
		LoginCode:    sess.LoginCode,
		PassCode:     sess.PassCode,
		ExpiresInSec: expiresIn,
		WSToken:      h.tokens.Generate(sess.ID),
		TurnServers:  h.generateTurnServers(sess.ID),
	})
}

func (h *Handler) health(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

// status — публичный endpoint для status page и "любопытных".
// Возвращает минимум public info (без sensitive данных):
//   - status: "ok" / "maintenance" (если админ включил maintenance mode)
//   - uptime_seconds: сколько секунд процесс живёт
//   - started_at_unix: Unix timestamp запуска (для client-side uptime calculation)
//   - server_time_unix: текущее время сервера (для sync checks)
//   - version: версия server (hardcoded — в будущем можно заменить на build flag)
//
// НЕ возвращается: session count, peer count, IP банлист — всё это внутреннее
// состояние, которое может использоваться attacker'ом для enumeration.
// Status page хочет знать "жив ли сервер" и показать аптайм — этого достаточно.
//
// CORS: открытый endpoint, без Origin check — позволяет status badges / monitoring
// из любых источников (uptime robots, пользовательские dashboards).
func (h *Handler) status(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		writeJSON(w, http.StatusMethodNotAllowed, map[string]string{"error": "method not allowed"})
		return
	}
	now := time.Now()
	uptime := int64(now.Sub(h.startedAt).Seconds())

	// Maintenance mode info — public (status page должна показывать что сервер
	// временно не принимает new sessions). Само сообщение admin'а не leak'им.
	serverStatus := "ok"
	maintenance := false
	if h.maintenanceCheck != nil {
		if on, _ := h.maintenanceCheck(); on {
			serverStatus = "maintenance"
			maintenance = true
		}
	}

	// CORS open — это public endpoint. Разрешаем origin '*' только для GET.
	w.Header().Set("Access-Control-Allow-Origin", "*")
	// Cache на 5 секунд — landing page polls раз в 10 сек, так что cache hit ratio
	// высокий. Caddy (reverse proxy) и CDNs absorb polling — защита от bot attack
	// spam'щего /status для DDoS amplification (audit LOW finding).
	// Short TTL чтобы uptime counter на status page оставался live-ish.
	w.Header().Set("Cache-Control", "public, max-age=5")

	writeJSON(w, http.StatusOK, map[string]any{
		"status":           serverStatus,
		"maintenance":      maintenance,
		"uptime_seconds":   uptime,
		"started_at_unix":  h.startedAt.Unix(),
		"server_time_unix": now.Unix(),
		"version":          "1.0.0", // TODO: заменить на build flag -X main.Version=...
	})
}

func writeJSON(w http.ResponseWriter, status int, data any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(data)
}

// DEPRECATED: используй h.realIP.Extract(r). Оставлено для compat со старыми call sites
// которые получили r, но не имеют доступа к h. Возвращает только direct RemoteAddr.
func clientIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}
