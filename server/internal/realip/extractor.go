// Package realip extracts the real client IP из HTTP request с учётом reverse proxy.
// Shared между api/handler.go и ratelimit/middleware.go — раньше каждый делал свой
// half-broken impl. F-07 audit 2026-04-24 consolidated into single implementation.
//
// Core problem: signaling сидит за Caddy → r.RemoteAddr shows Caddy's docker IP
// (172.18.0.4), а не client'а. Real IP приходит в X-Real-IP / X-Forwarded-For headers
// которые Caddy ставит (см. deploy/Caddyfile.template header_up). Мы trust'им эти
// headers ТОЛЬКО если direct connection от trusted proxy — иначе attacker спуфит.
//
// TRUSTED_PROXIES env: CIDR или exact IP, comma-separated.
// Docker default: "172.16.0.0/12" (172.16.0.0 - 172.31.255.255).
package realip

import (
	"net"
	"net/http"
	"strings"
)

// Extractor извлекает реальный IP клиента из HTTP request с учётом trusted proxy headers.
type Extractor struct {
	// Pre-parsed trusted networks. nil если TRUSTED_PROXIES не задан.
	nets []*net.IPNet
	ips  map[string]bool
}

// NewExtractor парсит trustedProxies list (exact IPs + CIDRs) в efficient
// lookup structure. Invalid entries silently skip'аются.
// New parses trustedProxies (exact IPs + CIDRs) into efficient lookup structure.
// Invalid entries silently skip'аются (no panic).
func New(trustedProxies []string) *Extractor {
	ex := &Extractor{
		ips: make(map[string]bool),
	}
	for _, p := range trustedProxies {
		p = strings.TrimSpace(p)
		if p == "" {
			continue
		}
		if strings.Contains(p, "/") {
			// CIDR
			_, n, err := net.ParseCIDR(p)
			if err == nil {
				ex.nets = append(ex.nets, n)
			}
		} else {
			// Exact IP
			if ip := net.ParseIP(p); ip != nil {
				ex.ips[ip.String()] = true
			}
		}
	}
	return ex
}

// Extract возвращает реальный IP клиента. Format: canonical IP string (без порта).
// IPv6 addresses возвращаются без brackets (e.g. "2001:db8::1", не "[2001:db8::1]").
func (ex *Extractor) Extract(r *http.Request) string {
	remoteHost, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		remoteHost = r.RemoteAddr
	}

	// Если direct connection от trusted proxy — trust XFF/X-Real-IP
	if ex.isTrusted(remoteHost) {
		// X-Real-IP приоритет (single value, cleaner format от Caddy's header_up X-Real-IP)
		if xri := strings.TrimSpace(r.Header.Get("X-Real-IP")); xri != "" {
			return xri
		}
		// Fallback на X-Forwarded-For (можно быть comma-separated chain — берём first = client'а)
		if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
			if idx := strings.Index(xff, ","); idx >= 0 {
				return strings.TrimSpace(xff[:idx])
			}
			return strings.TrimSpace(xff)
		}
	}
	return remoteHost
}

// isTrusted проверяет что ip в списке trusted proxies (exact или CIDR match).
func (ex *Extractor) isTrusted(ipStr string) bool {
	if ex.ips[ipStr] {
		return true
	}
	if len(ex.nets) == 0 {
		return false
	}
	ip := net.ParseIP(ipStr)
	if ip == nil {
		return false
	}
	for _, n := range ex.nets {
		if n.Contains(ip) {
			return true
		}
	}
	return false
}
