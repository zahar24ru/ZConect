package turn

import (
	"crypto/hmac"
	"crypto/sha1"
	"encoding/base64"
	"strconv"
	"strings"
	"testing"
	"time"
)

func TestGenerate_BasicShape(t *testing.T) {
	secret := "test-secret-123"
	cred := Generate(secret, "session-abc", 30*time.Minute)

	if cred.Username == "" {
		t.Fatal("Username empty")
	}
	if cred.Credential == "" {
		t.Fatal("Credential empty")
	}
	if cred.ExpiresAtUnix == 0 {
		t.Fatal("ExpiresAtUnix zero")
	}
	if cred.TTLSeconds != 1800 {
		t.Errorf("TTLSeconds = %d, want 1800", cred.TTLSeconds)
	}

	// Username должен быть в формате <ts>:session-abc
	parts := strings.SplitN(cred.Username, ":", 2)
	if len(parts) != 2 {
		t.Fatalf("Username format invalid: %q", cred.Username)
	}
	if parts[1] != "session-abc" {
		t.Errorf("session ID in username = %q, want session-abc", parts[1])
	}
}

func TestGenerate_EmptySecret(t *testing.T) {
	// Если secret пустой — возвращаем zero-value Credential (rotating TURN отключён).
	cred := Generate("", "session-abc", 30*time.Minute)
	if cred.Username != "" || cred.Credential != "" {
		t.Errorf("empty secret должен вернуть zero Credential, got %+v", cred)
	}
}

func TestGenerate_HMACCorrect(t *testing.T) {
	// Проверяем что credential = base64(HMAC-SHA1(secret, username)) — именно этот
	// формат coturn ожидает. Если эта проверка сломается, coturn будет reject'ить.
	secret := "shared-secret-abc"
	cred := Generate(secret, "mysession", 10*time.Minute)

	mac := hmac.New(sha1.New, []byte(secret))
	mac.Write([]byte(cred.Username))
	expected := base64.StdEncoding.EncodeToString(mac.Sum(nil))

	if cred.Credential != expected {
		t.Errorf("credential mismatch:\n got:  %s\n want: %s", cred.Credential, expected)
	}
}

func TestGenerate_TTLClamping(t *testing.T) {
	secret := "s"
	// TTL слишком маленький → clamp до минимума
	c1 := Generate(secret, "x", 1*time.Second)
	if c1.TTLSeconds != int(MinCredentialTTL.Seconds()) {
		t.Errorf("tiny TTL не заклампился до min: %d", c1.TTLSeconds)
	}
	// TTL слишком большой → clamp до max
	c2 := Generate(secret, "x", 48*time.Hour)
	if c2.TTLSeconds != int(MaxCredentialTTL.Seconds()) {
		t.Errorf("huge TTL не заклампился до max: %d", c2.TTLSeconds)
	}
}

func TestGenerate_SessionIDSanitization(t *testing.T) {
	// Session ID содержащий ':' разломал бы парсинг username coturn'ом.
	secret := "s"
	cred := Generate(secret, "abc:def:ghi", 30*time.Minute)
	// Первый ':' разделяет expiration от остального — всё после первого ':'
	// это session ID. Внутренние ':' должны быть заменены на '-'.
	parts := strings.SplitN(cred.Username, ":", 2)
	if strings.Contains(parts[1], ":") {
		t.Errorf("session ID в username содержит ':' — потенциальная коллизия парсинга: %q", cred.Username)
	}
}

func TestVerify_Roundtrip(t *testing.T) {
	secret := "roundtrip-secret"
	cred := Generate(secret, "sessionXYZ", 30*time.Minute)
	if !Verify(secret, cred.Username, cred.Credential) {
		t.Error("freshly generated cred failed verification")
	}
}

func TestVerify_WrongSecret(t *testing.T) {
	cred := Generate("correct", "s", 30*time.Minute)
	if Verify("wrong", cred.Username, cred.Credential) {
		t.Error("wrong secret passed verification — SECURITY BUG")
	}
}

func TestVerify_TamperedCredential(t *testing.T) {
	secret := "s"
	cred := Generate(secret, "s", 30*time.Minute)
	tampered := cred.Credential[:len(cred.Credential)-4] + "AAAA"
	if Verify(secret, cred.Username, tampered) {
		t.Error("tampered credential passed — HMAC verify broken")
	}
}

func TestVerify_ExpiredRejected(t *testing.T) {
	secret := "s"
	// Constructing credential вручную с уже истёкшим timestamp.
	// Нельзя просто подождать — MinCredentialTTL = 5 мин, слишком долго.
	// Clamping защищает от sub-5-min TTL в Generate, так что test rigs делаем руками.
	expiredTs := time.Now().Add(-10 * time.Second).Unix()
	expiredUsername := strconv.FormatInt(expiredTs, 10) + ":test-session"

	// Recompute HMAC для expired username (иначе Verify провалится на HMAC check
	// раньше чем на expiry — мы хотим убедиться что именно expiry check triggers).
	mac := hmac.New(sha1.New, []byte(secret))
	mac.Write([]byte(expiredUsername))
	expiredCred := base64.StdEncoding.EncodeToString(mac.Sum(nil))

	if Verify(secret, expiredUsername, expiredCred) {
		t.Error("expired credential passed verification — expiry check не работает")
	}
}

func TestVerify_MalformedUsername(t *testing.T) {
	secret := "s"
	// No colon at all
	if Verify(secret, "nocolon", "AAAA") {
		t.Error("username without ':' accepted")
	}
	// Non-numeric expiration
	if Verify(secret, "notanumber:session", "AAAA") {
		t.Error("non-numeric expiration accepted")
	}
}

func TestGenerateSecret_RandomUnique(t *testing.T) {
	s1, err := GenerateSecret()
	if err != nil {
		t.Fatal(err)
	}
	s2, err := GenerateSecret()
	if err != nil {
		t.Fatal(err)
	}
	if s1 == s2 {
		t.Error("GenerateSecret returned same value twice — RNG broken?")
	}
	if len(s1) != 64 {
		t.Errorf("secret length = %d, want 64 (32 bytes hex)", len(s1))
	}
	// Должен быть valid hex
	for _, c := range s1 {
		if !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) {
			t.Errorf("secret contains non-hex char: %c", c)
		}
	}
}
