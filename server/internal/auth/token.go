package auth

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"strconv"
	"strings"
	"time"
)

// TokenService generates and validates short-lived HMAC tokens for WebSocket auth.
type TokenService struct {
	secret []byte
	ttl    time.Duration
}

// NewTokenService creates a TokenService with a random 32-byte secret.
// Tokens are valid for the given TTL (e.g. 60 seconds).
func NewTokenService(ttl time.Duration) *TokenService {
	secret := make([]byte, 32)
	if _, err := rand.Read(secret); err != nil {
		panic("failed to generate token secret: " + err.Error())
	}
	return &TokenService{secret: secret, ttl: ttl}
}

// Generate creates a token for the given session ID.
// Format: "sessionId.timestampUnix.hmacHex"
func (ts *TokenService) Generate(sessionID string) string {
	now := time.Now().Unix()
	payload := fmt.Sprintf("%s.%d", sessionID, now)
	mac := hmac.New(sha256.New, ts.secret)
	mac.Write([]byte(payload))
	sig := hex.EncodeToString(mac.Sum(nil))
	return fmt.Sprintf("%s.%s", payload, sig)
}

// Validate checks that the token is well-formed, not expired, and signed correctly.
// Returns the session ID from the token on success.
func (ts *TokenService) Validate(token string) (string, bool) {
	parts := strings.SplitN(token, ".", 3)
	if len(parts) != 3 {
		return "", false
	}

	sessionID := parts[0]
	tsStr := parts[1]
	sig := parts[2]

	// Check timestamp.
	unix, err := strconv.ParseInt(tsStr, 10, 64)
	if err != nil {
		return "", false
	}
	issued := time.Unix(unix, 0)
	now := time.Now()

	// Audit fix 2026-04-25 (CRIT-2): защита от future timestamp.
	// Раньше: только `time.Since(issued) > ttl` — если issued в будущем
	// (clock skew/jump backwards/manual injection), Since возвращает
	// negative duration → проверка проходит → fail-open, токен валиден
	// "вечно" пока now не догонит issued.
	// Теперь: reject если issued > now + clockSkew (60 сек tolerance для
	// minor server clock drift) + традиционный TTL expiry check.
	const clockSkew = 60 * time.Second
	if issued.After(now.Add(clockSkew)) {
		return "", false
	}
	if now.Sub(issued) > ts.ttl {
		return "", false
	}

	// Verify HMAC.
	payload := fmt.Sprintf("%s.%d", sessionID, unix)
	mac := hmac.New(sha256.New, ts.secret)
	mac.Write([]byte(payload))
	expected := hex.EncodeToString(mac.Sum(nil))

	if !hmac.Equal([]byte(sig), []byte(expected)) {
		return "", false
	}

	return sessionID, true
}
