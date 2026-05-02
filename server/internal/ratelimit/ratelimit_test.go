package ratelimit

import (
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestAllow_WithinLimit(t *testing.T) {
	l := New(3, time.Minute, nil)

	for i := 0; i < 3; i++ {
		if !l.Allow("1.2.3.4") {
			t.Errorf("attempt %d должен быть allowed (лимит 3)", i+1)
		}
	}
}

func TestAllow_ExceedsLimit(t *testing.T) {
	l := New(3, time.Minute, nil)

	// Первые 3 разрешены
	for i := 0; i < 3; i++ {
		l.Allow("1.2.3.4")
	}
	// 4-й — rejected
	if l.Allow("1.2.3.4") {
		t.Error("4-й request сверх лимита 3 должен быть rejected")
	}
	// И 5-й, 6-й...
	if l.Allow("1.2.3.4") {
		t.Error("5-й тоже должен быть rejected")
	}
}

func TestAllow_WindowReset(t *testing.T) {
	// Очень короткое окно для быстрого теста
	l := New(2, 100*time.Millisecond, nil)

	if !l.Allow("1.2.3.4") {
		t.Error("1-й: allowed")
	}
	if !l.Allow("1.2.3.4") {
		t.Error("2-й: allowed")
	}
	if l.Allow("1.2.3.4") {
		t.Error("3-й: rejected")
	}

	// Ждём окна
	time.Sleep(150 * time.Millisecond)

	// После reset — снова allowed
	if !l.Allow("1.2.3.4") {
		t.Error("после window reset: allowed")
	}
}

// Разные IPs имеют независимые счётчики.
func TestAllow_PerIPIsolated(t *testing.T) {
	l := New(2, time.Minute, nil)

	// IP A — исчерпал лимит
	l.Allow("1.1.1.1")
	l.Allow("1.1.1.1")
	if l.Allow("1.1.1.1") {
		t.Error("1.1.1.1 должен быть rejected")
	}

	// IP B — не затронут
	if !l.Allow("2.2.2.2") {
		t.Error("2.2.2.2 должен быть allowed (independent counter)")
	}
}

// Middleware возвращает 429 на rate limit hit.
func TestMiddleware_Returns429OnExceed(t *testing.T) {
	l := New(2, time.Minute, nil)

	handlerCalled := 0
	handler := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		handlerCalled++
		w.WriteHeader(http.StatusOK)
	})
	mw := l.Middleware(handler)

	makeRequest := func() int {
		req := httptest.NewRequest("GET", "/test", nil)
		req.RemoteAddr = "5.5.5.5:12345"
		rec := httptest.NewRecorder()
		mw.ServeHTTP(rec, req)
		return rec.Code
	}

	if code := makeRequest(); code != http.StatusOK {
		t.Errorf("1-й: code=%d, want 200", code)
	}
	if code := makeRequest(); code != http.StatusOK {
		t.Errorf("2-й: code=%d, want 200", code)
	}
	// 3-й — 429
	if code := makeRequest(); code != http.StatusTooManyRequests {
		t.Errorf("3-й: code=%d, want 429", code)
	}

	if handlerCalled != 2 {
		t.Errorf("downstream handler called %d times, want 2 (3-й заблокирован middleware)",
			handlerCalled)
	}
}

// Retry-After header присутствует на 429.
func TestMiddleware_IncludesRetryAfterHeader(t *testing.T) {
	l := New(1, time.Minute, nil)

	handler := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {})
	mw := l.Middleware(handler)

	req := httptest.NewRequest("GET", "/test", nil)
	req.RemoteAddr = "6.6.6.6:1111"
	mw.ServeHTTP(httptest.NewRecorder(), req)

	// 2-й request — rate limited
	req2 := httptest.NewRequest("GET", "/test", nil)
	req2.RemoteAddr = "6.6.6.6:1111"
	rec := httptest.NewRecorder()
	mw.ServeHTTP(rec, req2)

	if rec.Code != http.StatusTooManyRequests {
		t.Fatalf("code=%d, want 429", rec.Code)
	}
	if rec.Header().Get("Retry-After") == "" {
		t.Error("Retry-After header отсутствует на 429 response")
	}
}

// F-07: X-Forwarded-For респектится только от trusted proxies.
func TestExtractIP_XFFIgnoredFromUntrustedProxy(t *testing.T) {
	// Trusted proxy = 10.0.0.1, но request идёт от 50.50.50.50
	l := New(10, time.Minute, []string{"10.0.0.1"})

	req := httptest.NewRequest("GET", "/", nil)
	req.RemoteAddr = "50.50.50.50:1111"
	req.Header.Set("X-Forwarded-For", "1.1.1.1") // попытка спуфинга

	ip := l.realIP.Extract(req)
	if ip != "50.50.50.50" {
		t.Errorf("untrusted XFF source: ip=%q, want 50.50.50.50 (XFF ignored)", ip)
	}
}

// Trusted proxy — XFF респектится.
func TestExtractIP_XFFTrustedFromProxy(t *testing.T) {
	l := New(10, time.Minute, []string{"10.0.0.1"})

	req := httptest.NewRequest("GET", "/", nil)
	req.RemoteAddr = "10.0.0.1:80" // trusted proxy
	req.Header.Set("X-Forwarded-For", "8.8.8.8")

	ip := l.realIP.Extract(req)
	if ip != "8.8.8.8" {
		t.Errorf("trusted proxy XFF: ip=%q, want 8.8.8.8", ip)
	}
}

// XFF с multiple IPs — берём первый (оригинальный client).
func TestExtractIP_XFFMultipleIPs(t *testing.T) {
	l := New(10, time.Minute, []string{"10.0.0.1"})

	req := httptest.NewRequest("GET", "/", nil)
	req.RemoteAddr = "10.0.0.1:80"
	req.Header.Set("X-Forwarded-For", "8.8.8.8, 10.0.0.2, 10.0.0.1")

	ip := l.realIP.Extract(req)
	if ip != "8.8.8.8" {
		t.Errorf("ip=%q, want 8.8.8.8 (first in XFF chain = original client)", ip)
	}
}

// Пустой trustedProxies список → XFF игнорируется для любого source.
func TestExtractIP_NoTrustedProxies_XFFAlwaysIgnored(t *testing.T) {
	l := New(10, time.Minute, nil)

	req := httptest.NewRequest("GET", "/", nil)
	req.RemoteAddr = "5.5.5.5:1111"
	req.Header.Set("X-Forwarded-For", "1.2.3.4")

	ip := l.realIP.Extract(req)
	if ip != "5.5.5.5" {
		t.Errorf("no trusted proxies: ip=%q, want 5.5.5.5", ip)
	}
}

// Concurrent Allow с разных goroutines — нет data race.
func TestAllow_Concurrent(t *testing.T) {
	l := New(1000, time.Minute, nil)

	const N = 200
	done := make(chan struct{}, N)
	for i := 0; i < N; i++ {
		go func() {
			l.Allow("1.2.3.4")
			done <- struct{}{}
		}()
	}
	for i := 0; i < N; i++ {
		<-done
	}

	// Проверяем что counter корректно посчитан: lastReset=<одно-время>, tokens = 1000 - N
	l.mu.Lock()
	v := l.visitors["1.2.3.4"]
	tokens := v.tokens
	l.mu.Unlock()

	if tokens != 1000-N {
		t.Errorf("tokens=%d, want %d (concurrent decrement)", tokens, 1000-N)
	}
}
