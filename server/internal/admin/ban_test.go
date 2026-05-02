package admin

import (
	"testing"
	"time"
)

// resetBanState — clear global bannedIPs map между tests. Тесты модифицируют
// package-level state, изолируем через helper.
func resetBanState() {
	bannedIPsMu.Lock()
	bannedIPs = make(map[string]BanEntry)
	bannedIPsMu.Unlock()
}

func TestIsIPBanned_UnknownIP(t *testing.T) {
	resetBanState()
	banned, reason := IsIPBanned("1.2.3.4")
	if banned {
		t.Errorf("незабаненный IP: want false, got true (reason=%q)", reason)
	}
	if reason != "" {
		t.Errorf("empty reason для non-banned, got %q", reason)
	}
}

func TestIsIPBanned_PermanentBan(t *testing.T) {
	resetBanState()
	// Ручной ban (без ExpiresAt = permanent)
	bannedIPsMu.Lock()
	bannedIPs["1.2.3.4"] = BanEntry{
		IP:        "1.2.3.4",
		Reason:    "manual permaban",
		CreatedAt: time.Now().UTC(),
	}
	bannedIPsMu.Unlock()

	banned, reason := IsIPBanned("1.2.3.4")
	if !banned {
		t.Fatal("want banned=true")
	}
	if reason != "manual permaban" {
		t.Errorf("reason=%q, want 'manual permaban'", reason)
	}
}

// AutoBan с TTL — после expiry IsIPBanned возвращает false И удаляет entry
// из map'ы (lazy cleanup).
func TestAutoBanIP_ExpiresAutomatically(t *testing.T) {
	resetBanState()

	AutoBanIP("5.6.7.8", "auto: brute-force", 100*time.Millisecond)

	// Immediately banned
	banned, _ := IsIPBanned("5.6.7.8")
	if !banned {
		t.Fatal("auto-ban должен быть активен сразу")
	}

	// Wait for expiry
	time.Sleep(150 * time.Millisecond)

	banned, _ = IsIPBanned("5.6.7.8")
	if banned {
		t.Error("после TTL auto-ban должен expirate")
	}

	// Lazy cleanup — IsIPBanned должен удалить expired entry
	bannedIPsMu.RLock()
	_, still := bannedIPs["5.6.7.8"]
	bannedIPsMu.RUnlock()
	if still {
		t.Error("expired entry должен быть удалён из map'ы")
	}
}

// AutoBan второй раз для того же IP — обновляет ExpiresAt (продлевает).
func TestAutoBanIP_ExtendsExistingBan(t *testing.T) {
	resetBanState()

	AutoBanIP("9.9.9.9", "first", 1*time.Hour)

	bannedIPsMu.RLock()
	first := bannedIPs["9.9.9.9"]
	bannedIPsMu.RUnlock()

	time.Sleep(10 * time.Millisecond) // чтобы timestamps отличались

	AutoBanIP("9.9.9.9", "second", 2*time.Hour)

	bannedIPsMu.RLock()
	second := bannedIPs["9.9.9.9"]
	bannedIPsMu.RUnlock()

	if !second.ExpiresAt.After(first.ExpiresAt) {
		t.Errorf("второй AutoBan должен продлить ExpiresAt, first=%s second=%s",
			first.ExpiresAt, second.ExpiresAt)
	}
	if second.Reason != "second" {
		t.Errorf("reason=%q, want 'second' (override)", second.Reason)
	}
	if !second.Auto {
		t.Error("Auto flag должен быть true")
	}
}

// AutoBanIP с пустым IP — no-op (защита от bad input).
func TestAutoBanIP_EmptyIPIgnored(t *testing.T) {
	resetBanState()
	AutoBanIP("", "test", time.Hour)

	bannedIPsMu.RLock()
	count := len(bannedIPs)
	bannedIPsMu.RUnlock()
	if count != 0 {
		t.Errorf("empty IP не должен создавать entry, got %d entries", count)
	}
}

// Permanent ban (ExpiresAt=zero) не удаляется при IsIPBanned lookup'е.
func TestIsIPBanned_PermanentBanNotRemoved(t *testing.T) {
	resetBanState()
	bannedIPsMu.Lock()
	bannedIPs["10.0.0.1"] = BanEntry{
		IP:        "10.0.0.1",
		Reason:    "manual",
		CreatedAt: time.Now().UTC(),
		// ExpiresAt: zero → permanent
	}
	bannedIPsMu.Unlock()

	// Check 3 раза — не должен исчезать
	for i := 0; i < 3; i++ {
		banned, _ := IsIPBanned("10.0.0.1")
		if !banned {
			t.Fatalf("check %d: permanent ban должен остаться", i+1)
		}
	}

	bannedIPsMu.RLock()
	_, still := bannedIPs["10.0.0.1"]
	bannedIPsMu.RUnlock()
	if !still {
		t.Error("permanent ban не должен удаляться из map'ы")
	}
}

// Concurrent AutoBanIP из разных goroutines — нет data race.
func TestAutoBanIP_Concurrent(t *testing.T) {
	resetBanState()

	const N = 100
	done := make(chan struct{}, N)
	for i := 0; i < N; i++ {
		go func(idx int) {
			ip := "192.168.1." + string(rune('0'+idx%10))
			AutoBanIP(ip, "concurrent test", time.Hour)
			done <- struct{}{}
		}(i)
	}
	for i := 0; i < N; i++ {
		<-done
	}

	bannedIPsMu.RLock()
	count := len(bannedIPs)
	bannedIPsMu.RUnlock()
	// Ожидаем 10 unique IPs (по числу %10 bucket'ов)
	if count != 10 {
		t.Errorf("concurrent AutoBan: got %d unique entries, want 10", count)
	}
}

// sanitizeReason обрезает control chars и truncate'ит до 200.
func TestSanitizeReason_StripsControlChars(t *testing.T) {
	input := "normal\nreason\twith\rcontrols"
	out := sanitizeReason(input)
	for _, c := range out {
		if c < 0x20 && c != ' ' {
			t.Errorf("control char %q остался в output %q", c, out)
		}
	}
	expected := "normal reason with controls"
	if out != expected {
		t.Errorf("out=%q, want %q", out, expected)
	}
}

func TestSanitizeReason_TruncatesLong(t *testing.T) {
	// 300 chars
	long := ""
	for i := 0; i < 300; i++ {
		long += "x"
	}
	out := sanitizeReason(long)
	if len(out) != 200 {
		t.Errorf("len=%d, want 200 (truncated)", len(out))
	}
}

func TestSanitizeReason_TrimSpace(t *testing.T) {
	out := sanitizeReason("   hello   ")
	if out != "hello" {
		t.Errorf("out=%q, want 'hello'", out)
	}
}
