package signaling

import (
	"crypto/rand"
	"fmt"
	"net/http"
	"time"

	"github.com/gorilla/websocket"
	"zconect/server/internal/logging"
	"zconect/server/internal/session"
)

type Handler struct {
	hub      *Hub
	sessions *session.Service
	logger   *logging.Logger
	upgrader websocket.Upgrader
}

func NewHandler(hub *Hub, sessions *session.Service, logger *logging.Logger) *Handler {
	return &Handler{
		hub:      hub,
		sessions: sessions,
		logger:   logger,
		upgrader: websocket.Upgrader{
			CheckOrigin: func(r *http.Request) bool { return true },
		},
	}
}

func (h *Handler) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	sessionID := r.URL.Query().Get("session_id")
	if sessionID == "" {
		http.Error(w, "session_id is required", http.StatusBadRequest)
		return
	}

	if _, err := h.sessions.Get(sessionID); err != nil {
		http.Error(w, "session not found", http.StatusNotFound)
		return
	}

	peers := h.hub.PeersInSession(sessionID)
	if len(peers) >= 2 {
		http.Error(w, "session already has two peers", http.StatusConflict)
		return
	}

	conn, err := h.upgrader.Upgrade(w, r, nil)
	if err != nil {
		h.logger.Log("ERROR", "WS", "upgrade_failed", "websocket upgrade failed", logging.Entry{Error: err.Error()})
		return
	}

	peer := &Peer{
		ID:        randomPeerID(),
		SessionID: sessionID,
		Conn:      conn,
	}
	h.hub.Add(peer)
	h.logger.Log("INFO", "WS", "peer_connected", "peer connected", logging.Entry{SessionID: sessionID})

	_ = conn.SetReadDeadline(time.Now().Add(60 * time.Second))
	conn.SetPongHandler(func(string) error {
		return conn.SetReadDeadline(time.Now().Add(60 * time.Second))
	})

	defer func() {
		_ = conn.Close()
		h.hub.Remove(sessionID, peer.ID)
		h.logger.Log("INFO", "WS", "peer_disconnected", "peer disconnected", logging.Entry{SessionID: sessionID})
	}()

	for {
		msgType, payload, err := conn.ReadMessage()
		if err != nil {
			return
		}
		targets := h.hub.PeersInSession(sessionID)
		for _, target := range targets {
			if target.ID == peer.ID {
				continue
			}
			_ = target.Conn.WriteMessage(msgType, payload)
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
