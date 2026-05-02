// Package admin — secure admin panel for server control.
//
// ARCHITECTURE
//
// Session-based auth (НЕ API key в URL). Admin flow:
//  1. Admin opens https://server/admin → no session → redirect /admin/login
//  2. POSTs password to /admin/api/login → bcrypt compare → session cookie
//  3. Cookie: HttpOnly, Secure (HTTPS only), SameSite=Strict, Path=/admin
//  4. All admin endpoints check session cookie + CSRF token for mutations
//  5. Logout clears session + cookie
//
// Threat model mitigations:
//  - Password: bcrypt (cost 12) — slow enough to resist offline brute force
//  - Brute force login: rate limited (5 attempts per IP per 15 min)
//  - Session theft: HttpOnly (JS can't read), Secure (only over TLS)
//  - CSRF: SameSite=Strict + double-submit CSRF token for mutations
//  - Credentials in logs: never logged; audit log has action+IP only
//  - Compromised networks: optional IP whitelist (ADMIN_ALLOWED_IPS env)
//  - Session expires: 1h idle, 8h absolute maximum
//  - MitM: HTTPS-only cookie (не передаётся по HTTP)
//
// NO 2FA / TOTP / password reset via email — intentional minimal scope.
// Password reset = SSH to server + change ADMIN_PASSWORD_HASH env.
package admin

import (
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"sync"
	"time"

	"golang.org/x/crypto/bcrypt"
)

// Session — opaque server-side state. Cookie carries only the session ID.
type Session struct {
	ID         string    // random 256-bit, base64 url-encoded
	CSRFToken  string    // random 256-bit, hex-encoded, sent in CSRF-Token header
	IP         string    // binding — if request IP changes, session is invalidated
	CreatedAt  time.Time // absolute expiry base
	LastActive time.Time // idle expiry base
}

// SessionStore — in-memory map; sessions lost on server restart (that's fine,
// admin re-logins). NO persistence — keeps attack surface minimal.
type SessionStore struct {
	mu          sync.RWMutex
	sessions    map[string]*Session
	idleTimeout time.Duration
	maxLifetime time.Duration
}

func NewSessionStore(idleTimeout, maxLifetime time.Duration) *SessionStore {
	return &SessionStore{
		sessions:    make(map[string]*Session),
		idleTimeout: idleTimeout,
		maxLifetime: maxLifetime,
	}
}

// Create — generates new session with fresh random ID and CSRF token.
// IP — client's remote addr, binds session to it.
func (s *SessionStore) Create(ip string) (*Session, error) {
	sid, err := randomToken(32) // 256 bits
	if err != nil {
		return nil, err
	}
	csrf, err := randomHex(32) // 256 bits
	if err != nil {
		return nil, err
	}
	now := time.Now().UTC()
	sess := &Session{
		ID:         sid,
		CSRFToken:  csrf,
		IP:         ip,
		CreatedAt:  now,
		LastActive: now,
	}
	s.mu.Lock()
	s.sessions[sid] = sess
	s.mu.Unlock()
	return sess, nil
}

// Validate — checks cookie matches live session, not expired, IP matches.
// Returns session on success (with LastActive refreshed).
func (s *SessionStore) Validate(sid, ip string) (*Session, error) {
	if sid == "" {
		return nil, errors.New("empty session id")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	sess, ok := s.sessions[sid]
	if !ok {
		return nil, errors.New("session not found")
	}
	now := time.Now().UTC()
	// Absolute max lifetime
	if now.Sub(sess.CreatedAt) > s.maxLifetime {
		delete(s.sessions, sid)
		return nil, errors.New("session expired (max lifetime)")
	}
	// Idle timeout
	if now.Sub(sess.LastActive) > s.idleTimeout {
		delete(s.sessions, sid)
		return nil, errors.New("session expired (idle)")
	}
	// IP binding — mitigate session hijacking across networks
	if !constantTimeStringEqual(sess.IP, ip) {
		delete(s.sessions, sid)
		return nil, errors.New("session ip mismatch")
	}
	sess.LastActive = now
	return sess, nil
}

// Destroy — logout; remove from store.
func (s *SessionStore) Destroy(sid string) {
	if sid == "" {
		return
	}
	s.mu.Lock()
	delete(s.sessions, sid)
	s.mu.Unlock()
}

// Gc — run periodically from caller's goroutine. Removes expired sessions.
func (s *SessionStore) Gc() int {
	now := time.Now().UTC()
	s.mu.Lock()
	defer s.mu.Unlock()
	removed := 0
	for sid, sess := range s.sessions {
		if now.Sub(sess.CreatedAt) > s.maxLifetime || now.Sub(sess.LastActive) > s.idleTimeout {
			delete(s.sessions, sid)
			removed++
		}
	}
	return removed
}

// CheckPassword — bcrypt compare. Constant-time, resistant to timing.
func CheckPassword(password, bcryptHash string) bool {
	if bcryptHash == "" {
		return false
	}
	err := bcrypt.CompareHashAndPassword([]byte(bcryptHash), []byte(password))
	return err == nil
}

// HashPassword — generate new bcrypt hash (used by admin-hashpass CLI).
func HashPassword(password string) (string, error) {
	if len(password) < 8 {
		return "", errors.New("password must be at least 8 characters")
	}
	h, err := bcrypt.GenerateFromPassword([]byte(password), 12)
	if err != nil {
		return "", err
	}
	return string(h), nil
}

// ── helpers ────────────────────────────────────────────────────

func randomToken(nBytes int) (string, error) {
	b := make([]byte, nBytes)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(b), nil
}

func randomHex(nBytes int) (string, error) {
	b := make([]byte, nBytes)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return hex.EncodeToString(b), nil
}

// constantTimeStringEqual — constant-time string compare for IP binding.
func constantTimeStringEqual(a, b string) bool {
	return subtle.ConstantTimeCompare([]byte(a), []byte(b)) == 1
}
