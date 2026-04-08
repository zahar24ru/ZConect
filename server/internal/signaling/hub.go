package signaling

import (
	"sync"

	"github.com/gorilla/websocket"
)

type Peer struct {
	ID        string
	SessionID string
	Conn      *websocket.Conn
	writeMu   sync.Mutex
}

// WriteMessage sends a message to the peer. Gorilla WebSocket requires all
// concurrent writes to be serialized; this method enforces that via writeMu.
func (p *Peer) WriteMessage(msgType int, data []byte) error {
	p.writeMu.Lock()
	defer p.writeMu.Unlock()
	return p.Conn.WriteMessage(msgType, data)
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

// TryAdd atomically adds a peer only if the session has fewer than maxPeers.
// Returns false if the session is already full.
func (h *Hub) TryAdd(p *Peer, maxPeers int) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	room, ok := h.rooms[p.SessionID]
	if ok && len(room) >= maxPeers {
		return false
	}
	if !ok {
		h.rooms[p.SessionID] = make(map[string]*Peer)
	}
	h.rooms[p.SessionID][p.ID] = p
	return true
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
