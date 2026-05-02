package auth

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"strings"
	"testing"
	"time"
)

func TestGenerate_Validate_Roundtrip(t *testing.T) {
	ts := NewTokenService(60 * time.Second)
	tok := ts.Generate("session-abc")
	got, ok := ts.Validate(tok)
	if !ok {
		t.Fatal("freshly generated token failed validation")
	}
	if got != "session-abc" {
		t.Errorf("session id mismatch: got %q want session-abc", got)
	}
}

func TestValidate_Malformed(t *testing.T) {
	ts := NewTokenService(60 * time.Second)
	cases := []string{
		"",
		"not-a-token",
		"a.b",                                                 // missing third part
		"a.notnumber.deadbeef",                                // non-numeric ts
		"a." + fmt.Sprintf("%d", time.Now().Unix()) + ".sig", // wrong sig
	}
	for _, c := range cases {
		if _, ok := ts.Validate(c); ok {
			t.Errorf("malformed token accepted: %q", c)
		}
	}
}

func TestValidate_Expired(t *testing.T) {
	ts := NewTokenService(60 * time.Second)
	// Manually craft expired token: issued 2 min ago.
	pastUnix := time.Now().Add(-2 * time.Minute).Unix()
	payload := fmt.Sprintf("session-x.%d", pastUnix)
	mac := hmac.New(sha256.New, ts.secret)
	mac.Write([]byte(payload))
	sig := hex.EncodeToString(mac.Sum(nil))
	tok := payload + "." + sig

	if _, ok := ts.Validate(tok); ok {
		t.Error("expired token accepted (should reject)")
	}
}

// CRIT-2 audit fix 2026-04-25: future timestamp must be rejected,
// иначе fail-open окно где negative time.Since() меньше TTL.
func TestValidate_FutureTimestamp_Rejected(t *testing.T) {
	ts := NewTokenService(60 * time.Second)
	// Timestamp 5 min в будущем — должен reject (clockSkew = 60s).
	futureUnix := time.Now().Add(5 * time.Minute).Unix()
	payload := fmt.Sprintf("session-y.%d", futureUnix)
	mac := hmac.New(sha256.New, ts.secret)
	mac.Write([]byte(payload))
	sig := hex.EncodeToString(mac.Sum(nil))
	tok := payload + "." + sig

	if _, ok := ts.Validate(tok); ok {
		t.Error("future timestamp token accepted — fail-open bug not fixed")
	}
}

// Slight clock skew (within 60s tolerance) должен accept'иться.
func TestValidate_MinorFutureSkew_Accepted(t *testing.T) {
	ts := NewTokenService(60 * time.Second)
	// Timestamp 30 sec в будущем — within 60s tolerance.
	futureUnix := time.Now().Add(30 * time.Second).Unix()
	payload := fmt.Sprintf("session-z.%d", futureUnix)
	mac := hmac.New(sha256.New, ts.secret)
	mac.Write([]byte(payload))
	sig := hex.EncodeToString(mac.Sum(nil))
	tok := payload + "." + sig

	if _, ok := ts.Validate(tok); !ok {
		t.Error("minor future skew (within 60s) rejected — должно accept'иться")
	}
}

// Past timestamp slightly in the future of TTL boundary — все ещё reject как expired.
func TestValidate_BoundaryExpiry(t *testing.T) {
	ts := NewTokenService(60 * time.Second)
	// 61 секунду назад — за пределами TTL (60s).
	pastUnix := time.Now().Add(-61 * time.Second).Unix()
	payload := fmt.Sprintf("session-bd.%d", pastUnix)
	mac := hmac.New(sha256.New, ts.secret)
	mac.Write([]byte(payload))
	sig := hex.EncodeToString(mac.Sum(nil))
	tok := payload + "." + sig

	if _, ok := ts.Validate(tok); ok {
		t.Error("token at TTL+1s accepted — boundary error")
	}
}

// Wrong secret in HMAC → reject regardless of valid timestamp.
func TestValidate_WrongSignature(t *testing.T) {
	ts := NewTokenService(60 * time.Second)
	nowUnix := time.Now().Unix()
	payload := fmt.Sprintf("session-q.%d", nowUnix)
	// Sign с different secret → mismatch.
	wrongMac := hmac.New(sha256.New, []byte("not-the-real-secret"))
	wrongMac.Write([]byte(payload))
	tok := payload + "." + hex.EncodeToString(wrongMac.Sum(nil))

	if _, ok := ts.Validate(tok); ok {
		t.Error("token with wrong signature accepted — HMAC verify broken")
	}
}

// Tampered session ID (но keep'нем same signature) → mismatch.
func TestValidate_TamperedSessionId(t *testing.T) {
	ts := NewTokenService(60 * time.Second)
	tok := ts.Generate("original")
	// Заменим session часть.
	tampered := "evil." + strings.SplitN(tok, ".", 2)[1]
	if _, ok := ts.Validate(tampered); ok {
		t.Error("tampered session id accepted")
	}
}
