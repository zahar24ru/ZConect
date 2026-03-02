package signaling

import (
	"sync"

	"github.com/gorilla/websocket"
)

type Peer struct {
	ID        string
	SessionID string
	Conn      *websocket.Conn
}

type Hub struct {
	mu    sync.RWMutex
	rooms map[string]map[string]*Peer
}

func NewHub() *Hub {
	return &Hub{
		rooms: make(map[string]map[string]*Peer),
	}
}

func (h *Hub) Add(p *Peer) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if _, ok := h.rooms[p.SessionID]; !ok {
		h.rooms[p.SessionID] = make(map[string]*Peer)
	}
	h.rooms[p.SessionID][p.ID] = p
}

func (h *Hub) Remove(sessionID, peerID string) {
	h.mu.Lock()
	defer h.mu.Unlock()
	room, ok := h.rooms[sessionID]
	if !ok {
		return
	}
	delete(room, peerID)
	if len(room) == 0 {
		delete(h.rooms, sessionID)
	}
}

func (h *Hub) PeersInSession(sessionID string) []*Peer {
	h.mu.RLock()
	defer h.mu.RUnlock()
	room, ok := h.rooms[sessionID]
	if !ok {
		return nil
	}
	out := make([]*Peer, 0, len(room))
	for _, p := range room {
		out = append(out, p)
	}
	return out
}
