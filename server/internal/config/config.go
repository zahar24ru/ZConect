package config

import (
	"os"
	"strconv"
	"strings"
	"time"
)

type Config struct {
	Port            string
	SessionTTL      time.Duration
	MaxJoinAttempts int
	LockDuration    time.Duration
	RateLimit       int      // max API requests per minute per IP
	AllowedOrigins  []string // empty = allow all (dev mode)
	TrustedProxies  []string // F-07: IPs allowed to set X-Forwarded-For
	AppEnv          string   // "prod" or "dev"
	DashboardAPIKey string   // DEPRECATED (hard-cut после admin panel); оставлено для noop legacy route
	TelemetryDBPath string   // path to telemetry SQLite database
	ClientVersionFile string // auto-update: path to client-version.json (empty = auto-update disabled)

	// Admin panel (session-based auth, /admin/*)
	AdminPasswordHash     string        // bcrypt hash (bootstrap); empty = admin disabled
	AdminPasswordHashFile string        // persistent hash file (override env если exists)
	AdminAllowedIPs       []string      // IP whitelist; empty = allow all (dev)
	AdminSessionIdleTTL   time.Duration // default 1h
	AdminSessionMaxLife   time.Duration // default 8h
	AdminAuditLog         string        // path; empty = no audit log
	AdminAuditLogMaxMB    int           // auto-rotate threshold (default 10)
	AdminAuditLogKeep     int           // old files to keep after rotate (default 5)
	AdminCookieSecure     bool          // true = prod (Secure cookie); default false

	// TURN rotating credentials (RFC 7635 / coturn --use-auth-secret mode).
	// Если TurnAuthSecret пуст — rotation отключён, клиент использует static
	// TurnUsername/TurnPassword (legacy mode). При включении — coturn должен быть
	// сконфигурирован с тем же secret (см. docker-compose.fast.yml).
	TurnAuthSecret   string        // shared HMAC secret. Generate: `openssl rand -hex 32`.
	TurnCredTTL      time.Duration // время жизни каждого credential (default 30m)
	TurnPublicHost   string        // публичный host/IP coturn'а для client ICE config (TURN_PUBLIC_HOST)
	TurnPublicPort   int           // TURN port (TURN_PUBLIC_PORT, default 3478)
	TurnRealm        string        // TURN realm (должен matcher coturn config)
}

func Load() Config {
	return Config{
		Port:            getenv("SIGNALING_PORT", "8080"),
		SessionTTL:      time.Duration(getenvInt("SESSION_TTL_SEC", 300)) * time.Second,
		MaxJoinAttempts: getenvInt("MAX_JOIN_ATTEMPTS", 5),
		LockDuration:    time.Duration(getenvInt("LOCK_MINUTES", 10)) * time.Minute,
		RateLimit:       getenvInt("RATE_LIMIT_PER_MIN", 200),
		AllowedOrigins:  parseOrigins(getenv("ALLOWED_ORIGINS", "")),
		TrustedProxies:  parseOrigins(getenv("TRUSTED_PROXIES", "")),
		AppEnv:          getenv("APP_ENV", "dev"),
		DashboardAPIKey:     getenv("DASHBOARD_API_KEY", ""),
		TelemetryDBPath:     getenv("TELEMETRY_DB_PATH", "telemetry.db"),
		ClientVersionFile:   getenv("CLIENT_VERSION_FILE", "client-version.json"),
		AdminPasswordHash:     getenv("ADMIN_PASSWORD_HASH", ""),
		AdminPasswordHashFile: getenv("ADMIN_PASSWORD_HASH_FILE", "/app/data/admin-password.hash"),
		AdminAllowedIPs:       parseOrigins(getenv("ADMIN_ALLOWED_IPS", "")),
		AdminSessionIdleTTL:   time.Duration(getenvInt("ADMIN_SESSION_IDLE_TTL_SEC", 3600)) * time.Second,
		AdminSessionMaxLife:   time.Duration(getenvInt("ADMIN_SESSION_MAX_LIFE_SEC", 28800)) * time.Second,
		AdminAuditLog:         getenv("ADMIN_AUDIT_LOG", "admin-audit.log"),
		AdminAuditLogMaxMB:    getenvInt("ADMIN_AUDIT_LOG_MAX_MB", 10),
		AdminAuditLogKeep:     getenvInt("ADMIN_AUDIT_LOG_KEEP", 5),
		AdminCookieSecure:     getenv("ADMIN_COOKIE_SECURE", "false") == "true",

		TurnAuthSecret: getenv("TURN_AUTH_SECRET", ""),
		TurnCredTTL:    time.Duration(getenvInt("TURN_CRED_TTL_SEC", 1800)) * time.Second,
		TurnPublicHost: getenv("TURN_PUBLIC_HOST", ""), // fallback: PUBLIC_IPV4 (см. turnServerURL ниже)
		TurnPublicPort: getenvInt("TURN_PUBLIC_PORT", 3478),
		TurnRealm:      getenv("TURN_REALM", "zconect.local"),
	}
}

// TurnPublicAddr возвращает host:port coturn'а для client ICE config.
// Если TURN_PUBLIC_HOST не задан — fallback на PUBLIC_IPV4 (coturn bind IP).
func (c Config) TurnPublicAddr() string {
	host := c.TurnPublicHost
	if host == "" {
		host = os.Getenv("PUBLIC_IPV4")
	}
	if host == "" {
		return ""
	}
	return host + ":" + strconv.Itoa(c.TurnPublicPort)
}

func parseOrigins(raw string) []string {
	if raw == "" {
		return nil
	}
	var origins []string
	for _, o := range strings.Split(raw, ",") {
		o = strings.TrimSpace(o)
		if o != "" {
			origins = append(origins, o)
		}
	}
	return origins
}

func getenv(key, fallback string) string {
	v := os.Getenv(key)
	if v == "" {
		return fallback
	}
	return v
}

func getenvInt(key string, fallback int) int {
	v := os.Getenv(key)
	if v == "" {
		return fallback
	}
	parsed, err := strconv.Atoi(v)
	if err != nil {
		return fallback
	}
	return parsed
}
