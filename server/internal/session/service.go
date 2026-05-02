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
	// StateBlocked — сессия permanently залочена из-за persistent brute-force
	// (достигла max LockoutTier). Любой Join возвращает ErrBlocked. Разблокируется
	// только через Refresh с regeneratePass=true (host обновляет pass_code →
	// сбрасывается counter + tier + state → Pairing). Это предотвращает
	// бесконечное брутфорсение одной сессии при NAT (не бьём всех за IP).
	StateBlocked State = "BLOCKED"
)

var (
	ErrSessionNotFound = errors.New("session not found")
	ErrBadCredentials  = errors.New("invalid login or password")
	ErrExpired         = errors.New("session expired")
	ErrLocked          = errors.New("session temporarily locked")
	ErrBlocked        = errors.New("session blocked due to repeated failed attempts")
	ErrForbidden      = errors.New("owner secret mismatch")
)

// Progressive lockout tiers. После каждых maxJoinAttempts wrong passcode'ов
// сессия блокируется на соответствующий tier duration. Tier инкрементируется,
// счётчик JoinAttempts ресетится. Если tier превышает len(lockoutTierDurations)
// — сессия становится StateBlocked permanent (host должен refresh password).
//
// Почему per-session а не per-IP: NAT/мобильный интернет могут иметь много
// legitimate users за одним IP. Ban-by-IP ломает UX. Per-session ban — атакер
// тратит всё больше времени на каждый lockcycle, и после 20 fails сессия
// мертва до refresh host'а.
//
// Total fails до блокировки при default maxJoinAttempts=5: 5 × 4 tiers = 20.
// Время пока брутфорсер упрётся в permanent block:
//   tier1 15с — 5 попыток
//   tier2 2m  — 5 попыток
//   tier3 15m — 5 попыток
//   tier4 1h  — 5 попыток
//   после — BLOCKED навсегда.
// При 10⁸ passcode space это почти нулевая вероятность подобрать за 20 попыток.
var lockoutTierDurations = []time.Duration{
	15 * time.Second,
	2 * time.Minute,
	15 * time.Minute,
	1 * time.Hour,
}

type Session struct {
	ID             string        `json:"session_id"`
	LoginCode      string        `json:"login_code"`
	PassCode       string        `json:"pass_code"`
	State          State         `json:"state"`
	RequireConfirm bool          `json:"require_confirm"`
	CreatedAt      time.Time     `json:"created_at"`
	ExpiresAt      time.Time     `json:"expires_at"`
	TTL            time.Duration `json:"-"` // original session lifetime, used by Refresh
	// JoinAttempts — per-current-tier counter (0..maxJoinAttempts-1). Ресетится
	// при переходе на следующий tier (lock триггерится → counter=0) и на success.
	JoinAttempts int `json:"join_attempts"`
	// TotalFailedAttempts — cumulative count за всё время жизни сессии. НЕ ресетится
	// на lock, только на successful join или password refresh. Для admin panel —
	// видит "эту сессию попытались 47 раз" вне зависимости от текущего tier.
	TotalFailedAttempts int `json:"total_failed_attempts"`
	// LockoutTier — 0 = fresh, 1 = после первого 5-fail lock'а, 2 = после второго, ...
	// Когда >= len(lockoutTierDurations) → StateBlocked permanent.
	LockoutTier  int       `json:"lockout_tier"`
	LockedUntil  time.Time `json:"locked_until"`
	MachineID    string    `json:"machine_id,omitempty"`
	DeviceSecret string    `json:"device_secret,omitempty"` // NET-03: device ownership proof for machine_id reuse
	OwnerSecret  string    `json:"owner_secret,omitempty"`  // F-03: ownership proof for close/refresh
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
	DeviceSecret   string // NET-03: proof of device ownership when reusing MachineID
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
					// NET-03 fix: require device_secret to reuse existing session.
					if existing.DeviceSecret != "" && existing.DeviceSecret != opts.DeviceSecret {
						// Wrong device secret — ignore machine_id, create new session.
						// Do NOT return existing session to prevent takeover.
					} else {
						// Existing session still alive + device ownership verified — return it.
						return clone(existing), nil
					}
				} else {
					// Old session expired/closed — clean up indexes.
					delete(s.sessionByLogin, existing.LoginCode)
					delete(s.sessionsByID, existingID)
					delete(s.sessionByMachine, opts.MachineID)
				}
			} else {
				delete(s.sessionByMachine, opts.MachineID)
			}
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

	// F-03 fix: generate owner secret for session ownership verification.
	ownerSecret, err := randomID()
	if err != nil {
		return nil, err
	}

	// NET-03: generate device secret for machine binding.
	var deviceSecret string
	if opts.MachineID != "" {
		if opts.DeviceSecret != "" {
			deviceSecret = opts.DeviceSecret // reuse provided secret
		} else {
			deviceSecret, err = randomID()
			if err != nil {
				return nil, err
			}
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
		DeviceSecret:   deviceSecret,
		OwnerSecret:    ownerSecret,
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
	// Permanent block — host должен refresh password чтобы разблокировать.
	// Не теряется state даже после unlock'а lockoutTier.
	if sess.State == StateBlocked {
		return nil, ErrBlocked
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
		sess.TotalFailedAttempts++
		// Хит maxJoinAttempts в текущем tier → escalate: ресетим per-tier counter
		// и применяем lock из следующего tier duration. Если tier уже исчерпан
		// (все длительности использованы) — PERMANENT block.
		if sess.JoinAttempts >= s.maxJoinAttempts {
			sess.JoinAttempts = 0
			sess.LockoutTier++
			if sess.LockoutTier > len(lockoutTierDurations) {
				// Исчерпали все tier'ы — block permanent, LockedUntil уже не нужен
				// (StateBlocked проверяется раньше чем now.Before(LockedUntil)).
				sess.State = StateBlocked
				sess.LockedUntil = time.Time{}
			} else {
				// tier 1..N используют соответствующий duration из массива (index tier-1).
				dur := lockoutTierDurations[sess.LockoutTier-1]
				sess.LockedUntil = now.Add(dur)
			}
		}
		return nil, ErrBadCredentials
	}

	// Success — полный reset brute-force state.
	sess.JoinAttempts = 0
	sess.TotalFailedAttempts = 0
	sess.LockoutTier = 0
	sess.State = StatePairing
	return clone(sess), nil
}

func (s *Service) Close(sessionID, ownerSecret string) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	sess, ok := s.sessionsByID[sessionID]
	if !ok {
		return ErrSessionNotFound
	}
	// F-03 fix: verify owner secret.
	if sess.OwnerSecret != ownerSecret {
		return ErrForbidden
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
func (s *Service) Refresh(sessionID, ownerSecret string, requestedTTLSec int, regeneratePass bool) (*Session, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	sess, ok := s.sessionsByID[sessionID]
	if !ok {
		return nil, ErrSessionNotFound
	}
	if sess.State == StateClosed {
		return nil, ErrSessionNotFound
	}
	// F-03 fix: verify owner secret.
	if sess.OwnerSecret != ownerSecret {
		return nil, ErrForbidden
	}
	now := time.Now().UTC()
	if now.After(sess.ExpiresAt) {
		sess.State = StateClosed
		return nil, ErrExpired
	}

	// Only regenerate pass_code when explicitly requested (manual refresh button).
	// WS reconnect should NOT change pass to avoid invalidating viewer's saved credentials.
	if regeneratePass {
		newPass, err := random8Digits()
		if err != nil {
			return nil, err
		}
		sess.PassCode = newPass
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

	sess.ExpiresAt = now.Add(refreshTTL)
	sess.JoinAttempts = 0
	sess.LockedUntil = time.Time{}

	// Password refresh сбрасывает все brute-force counters и разблокирует сессию
	// если она была StateBlocked. Это единственный путь для host'а разблокировать
	// permanent-locked сессию после затяжного брутфорса.
	if regeneratePass {
		sess.TotalFailedAttempts = 0
		sess.LockoutTier = 0
		if sess.State == StateBlocked {
			sess.State = StatePairing
		}
	}

	return clone(sess), nil
}

// ActiveCount returns the number of non-closed, non-expired sessions.
func (s *Service) ActiveCount() int {
	s.mu.RLock()
	defer s.mu.RUnlock()
	count := 0
	now := time.Now().UTC()
	for _, sess := range s.sessionsByID {
		if sess.State != StateClosed && now.Before(sess.ExpiresAt) {
			count++
		}
	}
	return count
}

// CheckLoginsOnline — проверяет какие из переданных login codes имеют сейчас
// активные (non-closed non-expired) sessions. Возвращает map login → online bool.
// Используется /api/v1/presence endpoint'ом для Live presence в Address Book.
func (s *Service) CheckLoginsOnline(logins []string) map[string]bool {
	now := time.Now().UTC()
	s.mu.RLock()
	defer s.mu.RUnlock()
	result := make(map[string]bool, len(logins))
	for _, login := range logins {
		sessID, ok := s.sessionByLogin[login]
		if !ok {
			result[login] = false
			continue
		}
		sess, ok := s.sessionsByID[sessID]
		if !ok {
			result[login] = false
			continue
		}
		result[login] = sess.State != StateClosed && now.Before(sess.ExpiresAt)
	}
	return result
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

// ListActive — snapshot всех активных (не CLOSED + не expired) sessions.
// Для admin panel "WebRTC Sessions" tab.
func (s *Service) ListActive() []Session {
	s.mu.RLock()
	defer s.mu.RUnlock()
	now := time.Now().UTC()
	result := make([]Session, 0, len(s.sessionsByID))
	for _, sess := range s.sessionsByID {
		if sess.State == StateClosed || now.After(sess.ExpiresAt) {
			continue
		}
		result = append(result, *clone(sess))
	}
	return result
}

// LockRemaining — для loginCode'а возвращает время оставшееся до снятия temp lock'а.
// Если сессии нет, она не locked, или время уже истекло — возвращает 0.
// Используется API handler'ом чтобы установить Retry-After header на 429 response.
// Не меняет state, thread-safe через RLock.
func (s *Service) LockRemaining(loginCode string) time.Duration {
	s.mu.RLock()
	defer s.mu.RUnlock()
	id, ok := s.sessionByLogin[loginCode]
	if !ok {
		return 0
	}
	sess, ok := s.sessionsByID[id]
	if !ok {
		return 0
	}
	remaining := time.Until(sess.LockedUntil)
	if remaining < 0 {
		return 0
	}
	return remaining
}

// KillSession — force-close конкретной session (admin action).
// Returns true если session был найден и закрыт.
func (s *Service) KillSession(sessionID string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	sess, ok := s.sessionsByID[sessionID]
	if !ok {
		return false
	}
	sess.State = StateClosed
	delete(s.sessionByLogin, sess.LoginCode)
	delete(s.sessionsByID, sessionID)
	if sess.MachineID != "" {
		delete(s.sessionByMachine, sess.MachineID)
	}
	return true
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
