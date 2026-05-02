package signaling

import (
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

type Peer struct {
	ID        string
	SessionID string
	Conn      *websocket.Conn
	writeMu   sync.Mutex
}

// WriteMessage sends a message to the peer with a write deadline.
// Gorilla WebSocket requires all concurrent writes to be serialized via writeMu.
// N7-02: 5-second write deadline prevents slow-receiver DoS.
func (p *Peer) WriteMessage(msgType int, data []byte) error {
	p.writeMu.Lock()
	defer p.writeMu.Unlock()
	_ = p.Conn.SetWriteDeadline(time.Now().Add(5 * time.Second))
	err := p.Conn.WriteMessage(msgType, data)
	_ = p.Conn.SetWriteDeadline(time.Time{}) // reset
	return err
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

// RoomCount returns the number of active session rooms.
func (h *Hub) RoomCount() int {
	h.mu.RLock()
	defer h.mu.RUnlock()
	return len(h.rooms)
}

// PeerCount returns the total number of connected peers across all sessions.
func (h *Hub) PeerCount() int {
	h.mu.RLock()
	defer h.mu.RUnlock()
	total := 0
	for _, room := range h.rooms {
		total += len(room)
	}
	return total
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
