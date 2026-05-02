package admin

import (
	"context"
	"crypto/subtle"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"
)

// contextKey — typed key to avoid collisions in request context.
type contextKey string

const sessionCtxKey contextKey = "admin.session"

// IPWhitelist — drops requests from IPs not in the list. Empty = allow all
// (dev mode). Config via ADMIN_ALLOWED_IPS env var, comma-separated.
//
// Audit fix 2026-04-24 (M-1): раньше — map-exact-match только. Теперь поддерживает
// CIDR ("10.0.0.0/8", "172.16.0.0/12") + exact IP, как у realip.Extractor. Позволяет
// задавать "all office IPs" через CIDR вместо enumeration каждого адреса.
type IPWhitelist struct {
	exactIPs map[string]bool
	cidrs    []*net.IPNet
	empty    bool // true if no entries — allow all
}

func NewIPWhitelist(ips []string) *IPWhitelist {
	w := &IPWhitelist{exactIPs: make(map[string]bool)}
	count := 0
	for _, raw := range ips {
		entry := strings.TrimSpace(raw)
		if entry == "" {
			continue
		}
		count++
		if strings.Contains(entry, "/") {
			// CIDR
			if _, n, err := net.ParseCIDR(entry); err == nil {
				w.cidrs = append(w.cidrs, n)
			}
			// Malformed CIDR silently skip — preserves noisy-input tolerance.
		} else {
			// Exact IP — normalize to canonical form (trim whitespace, lowercase for IPv6)
			if parsed := net.ParseIP(entry); parsed != nil {
				w.exactIPs[parsed.String()] = true
			}
			// Non-IP entry silently skip.
		}
	}
	w.empty = count == 0
	return w
}

// Middleware — if whitelist is empty, allow everything. Otherwise drop unknown IPs.
func (w *IPWhitelist) Middleware(next http.Handler, extractIP func(*http.Request) string) http.Handler {
	return http.HandlerFunc(func(rw http.ResponseWriter, r *http.Request) {
		if w.empty {
			next.ServeHTTP(rw, r)
			return
		}
		ip := extractIP(r)
		if !w.isAllowed(ip) {
			http.Error(rw, "forbidden", http.StatusForbidden)
			return
		}
		next.ServeHTTP(rw, r)
	})
}

// isAllowed проверяет IP против exact-list + CIDR-list.
func (w *IPWhitelist) isAllowed(ipStr string) bool {
	if w.exactIPs[ipStr] {
		return true
	}
	if len(w.cidrs) == 0 {
		return false
	}
	parsed := net.ParseIP(ipStr)
	if parsed == nil {
		return false
	}
	for _, n := range w.cidrs {
		if n.Contains(parsed) {
			return true
		}
	}
	return false
}

// LoginRateLimiter — per-IP login attempt limiter. 5 attempts per 15 min
// по default. Отдельный от general API limiter чтобы не блокировать обычные
// запросы когда кто-то brute-force'ит admin.
type LoginRateLimiter struct {
	mu       sync.Mutex
	attempts map[string][]time.Time
	limit    int
	window   time.Duration
}

func NewLoginRateLimiter(limit int, window time.Duration) *LoginRateLimiter {
	return &LoginRateLimiter{
		attempts: make(map[string][]time.Time),
		limit:    limit,
		window:   window,
	}
}

// Allow — true если можно попробовать login. Записывает попытку
// (вне зависимости от результата — чтобы не бояпущу through "правильными" паролями).
func (l *LoginRateLimiter) Allow(ip string) bool {
	now := time.Now().UTC()
	l.mu.Lock()
	defer l.mu.Unlock()
	// Cleanup старых записей
	list := l.attempts[ip]
	cutoff := now.Add(-l.window)
	kept := list[:0]
	for _, t := range list {
		if t.After(cutoff) {
			kept = append(kept, t)
		}
	}
	if len(kept) >= l.limit {
		l.attempts[ip] = kept
		return false
	}
	kept = append(kept, now)
	l.attempts[ip] = kept
	return true
}

// Reset — clears attempts for IP (вызывать на успешный login чтобы rate-limit
// не бил legitimate user после опечатки).
func (l *LoginRateLimiter) Reset(ip string) {
	l.mu.Lock()
	delete(l.attempts, ip)
	l.mu.Unlock()
}

// Gc — periodic cleanup. Returns number of entries removed.
func (l *LoginRateLimiter) Gc() int {
	now := time.Now().UTC()
	cutoff := now.Add(-l.window)
	l.mu.Lock()
	defer l.mu.Unlock()
	removed := 0
	for ip, list := range l.attempts {
		kept := list[:0]
		for _, t := range list {
			if t.After(cutoff) {
				kept = append(kept, t)
			}
		}
		if len(kept) == 0 {
			delete(l.attempts, ip)
			removed++
		} else if len(kept) != len(list) {
			l.attempts[ip] = kept
		}
	}
	return removed
}

// SessionFromContext — извлекает активную сессию из request context (доступно
// только после requireSession middleware).
func SessionFromContext(ctx context.Context) *Session {
	if s, ok := ctx.Value(sessionCtxKey).(*Session); ok {
		return s
	}
	return nil
}

// requireSession — middleware, требует валидной session cookie. Перенаправляет
// HTML requests на /admin/login, JSON возвращает 401.
func (h *Handler) requireSession(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		cookie, err := r.Cookie(sessionCookieName)
		if err != nil || cookie.Value == "" {
			h.unauthorized(w, r)
			return
		}
		sess, err := h.sessions.Validate(cookie.Value, h.extractIP(r))
		if err != nil {
			// Clear invalid cookie
			h.clearSessionCookie(w)
			h.unauthorized(w, r)
			return
		}
		ctx := context.WithValue(r.Context(), sessionCtxKey, sess)
		next(w, r.WithContext(ctx))
	}
}

// requireCSRF — дополнительно к session, проверяет CSRF token для mutations (POST/PUT/DELETE).
// Token в header "X-CSRF-Token" должен совпадать с session.CSRFToken (constant-time).
func (h *Handler) requireCSRF(next http.HandlerFunc) http.HandlerFunc {
	return h.requireSession(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodGet || r.Method == http.MethodHead || r.Method == http.MethodOptions {
			next(w, r)
			return
		}
		sess := SessionFromContext(r.Context())
		if sess == nil {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		token := r.Header.Get("X-CSRF-Token")
		if token == "" {
			h.audit.Log(Entry{
				IP: h.extractIP(r), Action: "csrf_check", Result: "denied",
				Reason: "token_missing",
				Metadata: map[string]string{"path": r.URL.Path, "method": r.Method},
			})
			http.Error(w, "csrf token missing", http.StatusForbidden)
			return
		}
		if subtle.ConstantTimeCompare([]byte(token), []byte(sess.CSRFToken)) != 1 {
			h.audit.Log(Entry{
				IP: h.extractIP(r), Action: "csrf_check", Result: "denied",
				Reason: "token_invalid",
				Metadata: map[string]string{"path": r.URL.Path, "method": r.Method},
			})
			http.Error(w, "csrf token invalid", http.StatusForbidden)
			return
		}
		next(w, r)
	})
}

// unauthorized — HTML request → 302 на /admin/login. JSON/API → 401.
func (h *Handler) unauthorized(w http.ResponseWriter, r *http.Request) {
	if wantsJSON(r) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	http.Redirect(w, r, "/admin/login", http.StatusFound)
}

func wantsJSON(r *http.Request) bool {
	accept := r.Header.Get("Accept")
	return len(accept) >= 16 && accept[:16] == "application/json"
}
