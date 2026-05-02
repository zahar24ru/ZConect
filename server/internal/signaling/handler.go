package signaling

import (
	"crypto/rand"
	"encoding/json"
	"fmt"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	"zconect/server/internal/auth"
	"zconect/server/internal/logging"
	"zconect/server/internal/session"
)

// NET-01 fix: server-only message types that clients must not be allowed to send.
var reservedServerTypes = map[string]bool{
	"peer_disconnected": true,
	"error":             true,
	"system_error":      true,
	"system_notice":     true,
	"auth":              true, // NET-06: auth is consumed by server, never relayed
}

const (
	// Ping interval должен быть < NAT idle timeout (часто 30s у consumer routers/ISP).
	// Снижено с 30s → 15s 2026-04-27 после диагностики: user видел drops каждые
	// ровно 30-31 sec с close 1006 (abnormal closure: unexpected EOF) — classic
	// NAT entry eviction. Server's ping at T=30s race'ил с NAT timeout, packet
	// dropped → connection silent dead. 15s даёт 2x margin под любой NAT.
	pingInterval    = 15 * time.Second
	readDeadline    = 60 * time.Second
	writeDeadline   = 10 * time.Second
	maxMessageSize  = 64 * 1024 // 64 KB — signaling messages are small JSON
	peerMsgRate     = 30        // max messages per second per peer
	peerMsgBurst    = 60        // burst allowance
)

type Handler struct {
	hub            *Hub
	sessions       *session.Service
	logger         *logging.Logger
	tokens         *auth.TokenService
	allowedOrigins []string // empty = allow all (dev mode)
	prodMode       bool     // F-08: deny origin by default in prod
	upgrader       websocket.Upgrader
}

func NewHandler(hub *Hub, sessions *session.Service, logger *logging.Logger, tokens *auth.TokenService, allowedOrigins []string) *Handler {
	return NewHandlerWithEnv(hub, sessions, logger, tokens, allowedOrigins, "dev")
}

func NewHandlerWithEnv(hub *Hub, sessions *session.Service, logger *logging.Logger, tokens *auth.TokenService, allowedOrigins []string, appEnv string) *Handler {
	h := &Handler{
		hub:            hub,
		sessions:       sessions,
		logger:         logger,
		tokens:         tokens,
		allowedOrigins: allowedOrigins,
		prodMode:       appEnv == "prod",
	}
	h.upgrader = websocket.Upgrader{
		CheckOrigin: h.checkOrigin,
	}
	return h
}

func (h *Handler) checkOrigin(r *http.Request) bool {
	origin := r.Header.Get("Origin")
	if origin == "" {
		// Non-browser clients (desktop apps) don't send Origin — allow.
		return true
	}
	// F-08 fix: in prod with no allowed origins, deny browser connections.
	if len(h.allowedOrigins) == 0 {
		if h.prodMode {
			h.logger.Log("WARN", "WS", "origin_rejected_prod", "denied origin in prod (no ALLOWED_ORIGINS): "+origin, logging.Entry{})
			return false
		}
		return true // dev mode: allow all
	}
	for _, allowed := range h.allowedOrigins {
		if strings.EqualFold(origin, allowed) {
			return true
		}
	}
	h.logger.Log("WARN", "WS", "origin_rejected", "rejected origin: "+origin, logging.Entry{})
	return false
}

func (h *Handler) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	sessionID := r.URL.Query().Get("session_id")
	if sessionID == "" {
		http.Error(w, "session_id is required", http.StatusBadRequest)
		return
	}

	// N7-01: reject expired or closed sessions.
	sess, err := h.sessions.Get(sessionID)
	if err != nil {
		http.Error(w, "session not found", http.StatusNotFound)
		return
	}
	if sess.State == "CLOSED" || time.Now().UTC().After(sess.ExpiresAt) {
		http.Error(w, "session expired or closed", http.StatusGone)
		return
	}

	conn, err := h.upgrader.Upgrade(w, r, nil)
	if err != nil {
		h.logger.Log("ERROR", "WS", "upgrade_failed", "websocket upgrade failed", logging.Entry{Error: err.Error()})
		return
	}

	// Enforce max incoming message size to prevent OOM.
	conn.SetReadLimit(maxMessageSize)

	// NET-06: validate token from first "auth" message (not query string).
	if !h.waitForAuth(conn, sessionID) {
		// Audit fix #3 2026-04-25: send explicit close code 4001 (auth failed) так
		// чтобы клиент мог distinguish "expired token, retry с fresh" от "network
		// failure / server down". Раньше silently closed → client retry'ит token
		// который точно так же expired → infinite loop.
		// 4001 — custom WS close code (4000-4999 reserved для application).
		_ = conn.WriteMessage(websocket.CloseMessage,
			websocket.FormatCloseMessage(4001, "auth token invalid or expired"))
		_ = conn.Close()
		return
	}

	peer := &Peer{
		ID:        randomPeerID(),
		SessionID: sessionID,
		Conn:      conn,
	}
	// Allow 3 peers temporarily: during reconnect the old peer's TCP may linger
	// for up to 60s. The stale peer will be cleaned up when its read loop fails.
	if !h.hub.TryAdd(peer, 3) {
		h.logger.Log("WARN", "WS", "session_full", "session has 3+ peers", logging.Entry{SessionID: sessionID})
		_ = conn.WriteMessage(websocket.CloseMessage,
			websocket.FormatCloseMessage(websocket.ClosePolicyViolation, "session full"))
		_ = conn.Close()
		return
	}
	h.logger.Log("INFO", "WS", "peer_connected", fmt.Sprintf("peer connected (peers=%d)", len(h.hub.PeersInSession(sessionID))), logging.Entry{SessionID: sessionID})

	_ = conn.SetReadDeadline(time.Now().Add(readDeadline))
	conn.SetPongHandler(func(string) error {
		return conn.SetReadDeadline(time.Now().Add(readDeadline))
	})
	// Bug fix 2026-04-27: client-initiated pings (от .NET ClientWebSocket
	// KeepAliveInterval=20s) — gorilla auto-pongs but не reset'ит read deadline.
	// Если client's auto-pong to server's ping не доходит (network/proxy issue),
	// server's readDeadline=60s expires → connection drops every ~60 sec даже при
	// активном client. Симптом из user logs 2026-04-27: ws_disconnected каждые
	// 38-58 sec, then ws_reconnect_success.
	// Fix: SetPingHandler — на client's incoming ping reset deadline + auto-pong.
	// Это делает server resilient к asymmetric ping/pong path failures.
	conn.SetPingHandler(func(message string) error {
		_ = conn.SetReadDeadline(time.Now().Add(readDeadline))
		// Best-effort auto-pong. Если write fails — connection всё равно alive
		// на read side (deadline reset уже выполнен). Не возвращаем error —
		// это закрыло бы connection, а network blip на pong write — recoverable.
		_ = conn.WriteControl(websocket.PongMessage, []byte(message), time.Now().Add(writeDeadline))
		return nil
	})

	// Ping goroutine: keeps the connection alive and triggers pong-based deadline resets.
	stopPing := make(chan struct{})
	go func() {
		ticker := time.NewTicker(pingInterval)
		defer ticker.Stop()
		for {
			select {
			case <-ticker.C:
				peer.writeMu.Lock()
				_ = conn.SetWriteDeadline(time.Now().Add(writeDeadline))
				err := conn.WriteMessage(websocket.PingMessage, nil)
				_ = conn.SetWriteDeadline(time.Time{})
				peer.writeMu.Unlock()
				if err != nil {
					return
				}
			case <-stopPing:
				return
			}
		}
	}()

	// Per-peer rate limiter (token bucket).
	rl := newPeerRateLimiter(peerMsgRate, peerMsgBurst)

	defer func() {
		close(stopPing)
		_ = conn.Close()
		h.hub.Remove(sessionID, peer.ID)
		h.logger.Log("INFO", "WS", "peer_disconnected", "peer disconnected", logging.Entry{SessionID: sessionID})

		// Notify remaining peers that this peer has left.
		// Security fix: используем json.Marshal вместо string concat, чтобы
		// peer.ID с кавычками или backslashes не мог injection'уть JSON properties
		// (например `peer.ID = `x","admin":"yes`).
		notice, noticeErr := json.Marshal(struct {
			Type   string `json:"type"`
			PeerID string `json:"peerId"`
		}{
			Type:   "peer_disconnected",
			PeerID: peer.ID,
		})
		if noticeErr == nil {
			for _, remaining := range h.hub.PeersInSession(sessionID) {
				_ = remaining.WriteMessage(websocket.TextMessage, notice)
			}
		}
	}()

	for {
		msgType, payload, err := conn.ReadMessage()
		if err != nil {
			// Diagnostic 2026-04-27: log exact reason disconnect (was silent).
			// Common: i/o timeout (read deadline) / use of closed network connection
			// (server shutdown) / 1006 abnormal close (client/proxy abrupt) /
			// 1000 normal close (client explicit). User logs показывали drops
			// каждые 30-60 sec — log поможет diff'нуть от ping timeout / Caddy.
			closeCode := 0
			closeReason := ""
			if ce, ok := err.(*websocket.CloseError); ok {
				closeCode = ce.Code
				closeReason = ce.Text
			}
			h.logger.Log("INFO", "WS", "read_loop_exit",
				fmt.Sprintf("err=%T msg=%s close_code=%d close_reason=%q peer=%s", err, err.Error(), closeCode, closeReason, peer.ID),
				logging.Entry{SessionID: sessionID})
			return
		}

		// Rate limit: drop excess messages and warn.
		if !rl.allow() {
			h.logger.Log("WARN", "WS", "peer_rate_limited", "dropping message from peer "+peer.ID, logging.Entry{SessionID: sessionID})
			continue
		}

		// NET-01 fix: block reserved server-only message types from clients.
		if msgType == websocket.TextMessage {
			var envelope struct {
				Type string `json:"type"`
			}
			if json.Unmarshal(payload, &envelope) == nil && reservedServerTypes[envelope.Type] {
				h.logger.Log("WARN", "WS", "reserved_type_blocked", "blocked client message type: "+envelope.Type, logging.Entry{SessionID: sessionID})
				continue
			}
		}

		targets := h.hub.PeersInSession(sessionID)
		for _, target := range targets {
			if target.ID == peer.ID {
				continue
			}
			if err := target.WriteMessage(msgType, payload); err != nil {
				// N7-02: slow receiver — close their connection to prevent DoS.
				_ = target.Conn.Close()
			}
		}
	}
}

// waitForAuth reads the first WS message and validates the auth token.
// Returns true if authentication succeeded, false otherwise.
func (h *Handler) waitForAuth(conn *websocket.Conn, sessionID string) bool {
	// 5-second deadline for the auth message.
	_ = conn.SetReadDeadline(time.Now().Add(5 * time.Second))

	_, payload, err := conn.ReadMessage()
	if err != nil {
		h.logger.Log("WARN", "WS", "auth_read_failed", "failed to read auth message", logging.Entry{SessionID: sessionID, Error: err.Error()})
		return false
	}

	var envelope struct {
		Type    string `json:"type"`
		Payload struct {
			Token string `json:"token"`
		} `json:"payload"`
	}
	if err := json.Unmarshal(payload, &envelope); err != nil || envelope.Type != "auth" {
		h.logger.Log("WARN", "WS", "auth_invalid_message", "first message is not auth", logging.Entry{SessionID: sessionID})
		return false
	}

	tokenSessionID, ok := h.tokens.Validate(envelope.Payload.Token)
	if !ok || tokenSessionID != sessionID {
		h.logger.Log("WARN", "WS", "auth_token_invalid", "invalid or expired token", logging.Entry{SessionID: sessionID})
		return false
	}

	// Reset deadline to normal read timeout.
	_ = conn.SetReadDeadline(time.Now().Add(readDeadline))
	return true
}

func randomPeerID() string {
	b := make([]byte, 8)
	if _, err := rand.Read(b); err != nil {
		return fmt.Sprintf("peer-%d", time.Now().UnixNano())
	}
	return fmt.Sprintf("%x", b)
}

// peerRateLimiter is a simple token-bucket rate limiter for a single peer connection.
type peerRateLimiter struct {
	mu       sync.Mutex
	tokens   float64
	maxBurst float64
	rate     float64 // tokens per second
	lastTime time.Time
}

func newPeerRateLimiter(rate, burst int) *peerRateLimiter {
	return &peerRateLimiter{
		tokens:   float64(burst),
		maxBurst: float64(burst),
		rate:     float64(rate),
		lastTime: time.Now(),
	}
}

func (r *peerRateLimiter) allow() bool {
	r.mu.Lock()
	defer r.mu.Unlock()

	now := time.Now()
	elapsed := now.Sub(r.lastTime).Seconds()
	r.lastTime = now
	r.tokens += elapsed * r.rate
	if r.tokens > r.maxBurst {
		r.tokens = r.maxBurst
	}
	if r.tokens < 1 {
		return false
	}
	r.tokens--
	return true
}
