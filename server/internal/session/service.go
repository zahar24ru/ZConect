package session

import (
	"crypto/rand"
	"errors"
	"fmt"
	"sync"
	"time"
)

type State string

const (
	StateCreated   State = "CREATED"
	StatePairing   State = "PAIRING"
	StateConnected State = "CONNECTED"
	StateClosed    State = "CLOSED"
)

var (
	ErrSessionNotFound = errors.New("session not found")
	ErrBadCredentials  = errors.New("invalid login or password")
	ErrExpired         = errors.New("session expired")
	ErrLocked          = errors.New("session temporarily locked")
)

type Session struct {
	ID             string        `json:"session_id"`
	LoginCode      string        `json:"login_code"`
	PassCode       string        `json:"pass_code"`
	State          State         `json:"state"`
	RequireConfirm bool          `json:"require_confirm"`
	CreatedAt      time.Time     `json:"created_at"`
	ExpiresAt      time.Time     `json:"expires_at"`
	TTL            time.Duration `json:"-"` // original session lifetime, used by Refresh
	JoinAttempts   int           `json:"join_attempts"`
	LockedUntil    time.Time     `json:"locked_until"`
	MachineID      string        `json:"machine_id,omitempty"`
}

// MaxCustomTTL is the maximum session lifetime a client may request (7 days).
const MaxCustomTTL = 7 * 24 * time.Hour

type Service struct {
	mu               sync.RWMutex
	sessionsByID     map[string]*Session
	sessionByLogin   map[string]string
	sessionByMachine map[string]string // machineID -> sessionID
	ttl              time.Duration
	maxJoinAttempts  int
	lockDuration     time.Duration
}

func NewService(ttl time.Duration, maxJoinAttempts int, lockDuration time.Duration) *Service {
	return &Service{
		sessionsByID:     make(map[string]*Session),
		sessionByLogin:   make(map[string]string),
		sessionByMachine: make(map[string]string),
		ttl:              ttl,
		maxJoinAttempts:  maxJoinAttempts,
		lockDuration:     lockDuration,
	}
}

// CreateOpts holds optional parameters for session creation.
type CreateOpts struct {
	RequireConfirm bool
	ExpiresInSec   int    // 0 means use server default TTL
	MachineID      string // persistent machine identifier; empty = no machine binding
}

func (s *Service) Create(requireConfirm bool) (*Session, error) {
	return s.CreateWithOpts(CreateOpts{RequireConfirm: requireConfirm})
}

func (s *Service) CreateWithOpts(opts CreateOpts) (*Session, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	now := time.Now().UTC()

	// If machine_id is provided, check for an existing active session and reuse its login code.
	if opts.MachineID != "" {
		if existingID, ok := s.sessionByMachine[opts.MachineID]; ok {
			if existing, ok2 := s.sessionsByID[existingID]; ok2 {
				if existing.State != StateClosed && now.Before(existing.ExpiresAt) {
					// Existing session still alive — return it.
					return clone(existing), nil
				}
				// Old session expired/closed — clean up indexes.
				delete(s.sessionByLogin, existing.LoginCode)
				delete(s.sessionsByID, existingID)
			}
			delete(s.sessionByMachine, opts.MachineID)
		}
	}

	id, err := randomID()
	if err != nil {
		return nil, err
	}

	login, err := s.uniqueCodeLocked()
	if err != nil {
		return nil, err
	}
	pass, err := random8Digits()
	if err != nil {
		return nil, err
	}

	// Determine TTL.
	ttl := s.ttl
	if opts.ExpiresInSec > 0 {
		custom := time.Duration(opts.ExpiresInSec) * time.Second
		if custom > MaxCustomTTL {
			custom = MaxCustomTTL
		}
		if custom > 0 {
			ttl = custom
		}
	}

	session := &Session{
		ID:             id,
		LoginCode:      login,
		PassCode:       pass,
		State:          StateCreated,
		RequireConfirm: opts.RequireConfirm,
		CreatedAt:      now,
		ExpiresAt:      now.Add(ttl),
		TTL:            ttl,
		MachineID:      opts.MachineID,
	}
	s.sessionsByID[id] = session
	s.sessionByLogin[login] = id
	if opts.MachineID != "" {
		s.sessionByMachine[opts.MachineID] = id
	}
	return clone(session), nil
}

func (s *Service) Join(loginCode, passCode string) (*Session, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	id, ok := s.sessionByLogin[loginCode]
	if !ok {
		return nil, ErrBadCredentials
	}
	sess, ok := s.sessionsByID[id]
	if !ok {
		return nil, ErrSessionNotFound
	}
	if sess.State == StateClosed {
		return nil, ErrSessionNotFound
	}
	now := time.Now().UTC()
	if now.After(sess.ExpiresAt) {
		sess.State = StateClosed
		return nil, ErrExpired
	}
	if now.Before(sess.LockedUntil) {
		return nil, ErrLocked
	}
	if sess.PassCode != passCode {
		sess.JoinAttempts++
		if sess.JoinAttempts >= s.maxJoinAttempts {
			sess.LockedUntil = now.Add(s.lockDuration)
			sess.JoinAttempts = 0
		}
		return nil, ErrBadCredentials
	}

	sess.JoinAttempts = 0
	sess.State = StatePairing
	return clone(sess), nil
}

func (s *Service) Close(sessionID string) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	sess, ok := s.sessionsByID[sessionID]
	if !ok {
		return ErrSessionNotFound
	}
	sess.State = StateClosed
	// Clean up all indexes so closed session cannot be rejoined.
	delete(s.sessionByLogin, sess.LoginCode)
	if sess.MachineID != "" {
		delete(s.sessionByMachine, sess.MachineID)
	}
	return nil
}

// Refresh regenerates the pass_code for an existing session (login_code stays the same)
// and extends its lifetime. If requestedTTLSec > 0, updates the session TTL (capped at MaxCustomTTL).
// Otherwise uses the session's original TTL.
func (s *Service) Refresh(sessionID string, requestedTTLSec int) (*Session, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	sess, ok := s.sessionsByID[sessionID]
	if !ok {
		return nil, ErrSessionNotFound
	}
	if sess.State == StateClosed {
		return nil, ErrSessionNotFound
	}
	now := time.Now().UTC()
	if now.After(sess.ExpiresAt) {
		sess.State = StateClosed
		return nil, ErrExpired
	}

	newPass, err := random8Digits()
	if err != nil {
		return nil, err
	}

	// Determine TTL: use requested if provided, else session's original, else global default.
	refreshTTL := sess.TTL
	if requestedTTLSec > 0 {
		custom := time.Duration(requestedTTLSec) * time.Second
		if custom > MaxCustomTTL {
			custom = MaxCustomTTL
		}
		refreshTTL = custom
		sess.TTL = custom // update session's TTL for future refreshes
	}
	if refreshTTL <= 0 {
		refreshTTL = s.ttl
	}

	sess.PassCode = newPass
	sess.ExpiresAt = now.Add(refreshTTL)
	sess.JoinAttempts = 0
	sess.LockedUntil = time.Time{}

	return clone(sess), nil
}

func (s *Service) Get(sessionID string) (*Session, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()

	sess, ok := s.sessionsByID[sessionID]
	if !ok {
		return nil, ErrSessionNotFound
	}
	return clone(sess), nil
}

func (s *Service) uniqueCodeLocked() (string, error) {
	for i := 0; i < 10; i++ {
		code, err := random8Digits()
		if err != nil {
			return "", err
		}
		if _, exists := s.sessionByLogin[code]; !exists {
			return code, nil
		}
	}
	return "", errors.New("failed to allocate unique login code")
}

func randomID() (string, error) {
	b := make([]byte, 16)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16]), nil
}

func random8Digits() (string, error) {
	n := make([]byte, 8)
	if _, err := rand.Read(n); err != nil {
		return "", err
	}
	for i := range n {
		n[i] = '0' + (n[i] % 10)
	}
	return string(n), nil
}

func clone(s *Session) *Session {
	c := *s
	return &c
}
