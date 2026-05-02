package telemetry

import (
	"encoding/json"
	"fmt"
	"net/http"
	"sync"
	"time"
)

// GeoResolver resolves IP addresses to 2-letter country codes using ip-api.com.
// Results are cached in memory for 24 hours.
type GeoResolver struct {
	mu    sync.RWMutex
	cache map[string]cacheEntry
	client *http.Client
}

type cacheEntry struct {
	country string
	expires time.Time
}

const (
	geoTTL       = 24 * time.Hour
	geoTimeout   = 3 * time.Second
	geoRateDelay = 1500 * time.Millisecond // stay under 45 req/min
)

// NewGeoResolver creates a new GeoResolver with in-memory cache.
func NewGeoResolver() *GeoResolver {
	return &GeoResolver{
		cache:  make(map[string]cacheEntry),
		client: &http.Client{Timeout: geoTimeout},
	}
}

// Resolve returns the 2-letter country code for the given IP.
// Returns "" on error or unknown.
func (g *GeoResolver) Resolve(ip string) string {
	if ip == "" || ip == "127.0.0.1" || ip == "::1" {
		return "LOCAL"
	}

	// Check cache
	g.mu.RLock()
	if entry, ok := g.cache[ip]; ok && time.Now().Before(entry.expires) {
		g.mu.RUnlock()
		return entry.country
	}
	g.mu.RUnlock()

	// Query ip-api.com
	country := g.lookup(ip)

	// Cache result
	g.mu.Lock()
	g.cache[ip] = cacheEntry{country: country, expires: time.Now().Add(geoTTL)}
	// Evict old entries if cache grows too large
	if len(g.cache) > 10000 {
		g.evictOldest()
	}
	g.mu.Unlock()

	return country
}

func (g *GeoResolver) lookup(ip string) string {
	resp, err := g.client.Get(fmt.Sprintf("http://ip-api.com/json/%s?fields=countryCode", ip))
	if err != nil {
		return ""
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return ""
	}
	var result struct {
		CountryCode string `json:"countryCode"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
		return ""
	}
	return result.CountryCode
}

func (g *GeoResolver) evictOldest() {
	// Remove expired entries
	now := time.Now()
	for ip, entry := range g.cache {
		if now.After(entry.expires) {
			delete(g.cache, ip)
		}
	}
}
