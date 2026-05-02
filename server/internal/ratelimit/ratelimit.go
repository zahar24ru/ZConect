package ratelimit

import (
	"net/http"
	"sync"
	"time"

	"zconect/server/internal/realip"
)

// Limiter is a per-IP sliding-window rate limiter.
type Limiter struct {
	mu       sync.Mutex
	visitors map[string]*visitor
	rate     int           // max requests per window
	window   time.Duration // window size
	realIP   *realip.Extractor // F-07: shared real-IP extractor (supports CIDR for Docker)
}

type visitor struct {
	tokens    int
	lastReset time.Time
}

// New creates a Limiter allowing rate requests per window per IP.
// trustedProxies — exact IPs или CIDRs (e.g. "172.16.0.0/12" для Docker bridge).
func New(rate int, window time.Duration, trustedProxies []string) *Limiter {
	l := &Limiter{
		visitors: make(map[string]*visitor),
		rate:     rate,
		window:   window,
		realIP:   realip.New(trustedProxies),
	}
	go l.cleanup()
	return l
}

// Allow returns true if the request from ip is within the limit.
func (l *Limiter) Allow(ip string) bool {
	l.mu.Lock()
	defer l.mu.Unlock()

	now := time.Now()
	v, ok := l.visitors[ip]
	if !ok {
		l.visitors[ip] = &visitor{tokens: l.rate - 1, lastReset: now}
		return true
	}

	if now.Sub(v.lastReset) >= l.window {
		v.tokens = l.rate - 1
		v.lastReset = now
		return true
	}

	if v.tokens > 0 {
		v.tokens--
		return true
	}
	return false
}

// Middleware wraps an http.Handler and rejects excess requests with 429.
func (l *Limiter) Middleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		ip := l.realIP.Extract(r)
		if !l.Allow(ip) {
			w.Header().Set("Content-Type", "application/json")
			w.Header().Set("Retry-After", "10")
			w.WriteHeader(http.StatusTooManyRequests)
			_, _ = w.Write([]byte(`{"error":"rate limit exceeded"}`))
			return
		}
		next.ServeHTTP(w, r)
	})
}

// cleanup removes stale entries every 5 minutes.
func (l *Limiter) cleanup() {
	ticker := time.NewTicker(5 * time.Minute)
	defer ticker.Stop()
	for range ticker.C {
		l.mu.Lock()
		cutoff := time.Now().Add(-2 * l.window)
		for ip, v := range l.visitors {
			if v.lastReset.Before(cutoff) {
				delete(l.visitors, ip)
			}
		}
		l.mu.Unlock()
	}
}
