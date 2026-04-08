package signaling

import (
	"crypto/rand"
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

const (
	pingInterval    = 30 * time.Second
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
	upgrader       websocket.Upgrader
}

func NewHandler(hub *Hub, sessions *session.Service, logger *logging.Logger, tokens *auth.TokenService, allowedOrigins []string) *Handler {
	h := &Handler{
		hub:            hub,
		sessions:       sessions,
		logger:         logger,
		tokens:         tokens,
		allowedOrigins: allowedOrigins,
	}
	h.upgrader = websocket.Upgrader{
		CheckOrigin: h.checkOrigin,
	}
	return h
}

func (h *Handler) checkOrigin(r *http.Request) bool {
	// If no origins configured, allow all (development mode).
	if len(h.allowedOrigins) == 0 {
		return true
	}
	origin := r.Header.Get("Origin")
	if origin == "" {
		// Non-browser clients (desktop apps) don't send Origin — allow.
		return true
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

	// Validate WS token.
	token := r.URL.Query().Get("token")
	if tokenSessionID, ok := h.tokens.Validate(token); !ok || tokenSessionID != sessionID {
		http.Error(w, "invalid or expired token", http.StatusUnauthorized)
		return
	}

	if _, err := h.sessions.Get(sessionID); err != nil {
		http.Error(w, "session not found", http.StatusNotFound)
		return
	}

	conn, err := h.upgrader.Upgrade(w, r, nil)
	if err != nil {
		h.logger.Log("ERROR", "WS", "upgrade_failed", "websocket upgrade failed", logging.Entry{Error: err.Error()})
		return
	}

	// Enforce max incoming message size to prevent OOM.
	conn.SetReadLimit(maxMessageSize)

	peer := &Peer{
		ID:        randomPeerID(),
		SessionID: sessionID,
		Conn:      conn,
	}
	// Atomic check-and-add: prevents race where two peers pass the limit check simultaneously.
	if !h.hub.TryAdd(peer, 2) {
		_ = conn.WriteMessage(websocket.CloseMessage,
			websocket.FormatCloseMessage(websocket.ClosePolicyViolation, "session full"))
		_ = conn.Close()
		return
	}
	h.logger.Log("INFO", "WS", "peer_connected", "peer connected", logging.Entry{SessionID: sessionID})

	_ = conn.SetReadDeadline(time.Now().Add(readDeadline))
	conn.SetPongHandler(func(string) error {
		return conn.SetReadDeadline(time.Now().Add(readDeadline))
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
		notice := []byte(`{"type":"peer_disconnected","peerId":"` + peer.ID + `"}`)
		for _, remaining := range h.hub.PeersInSession(sessionID) {
			_ = remaining.WriteMessage(websocket.TextMessage, notice)
		}
	}()

	for {
		msgType, payload, err := conn.ReadMessage()
		if err != nil {
			return
		}

		// Rate limit: drop excess messages and warn.
		if !rl.allow() {
			h.logger.Log("WARN", "WS", "peer_rate_limited", "dropping message from peer "+peer.ID, logging.Entry{SessionID: sessionID})
			continue
		}

		targets := h.hub.PeersInSession(sessionID)
		for _, target := range targets {
			if target.ID == peer.ID {
				continue
			}
			_ = target.WriteMessage(msgType, payload)
		}
	}
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
