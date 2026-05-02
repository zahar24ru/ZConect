package main

import (
	"context"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"syscall"
	"time"

	"zconect/server/internal/api"
	"zconect/server/internal/auth"
	"zconect/server/internal/bruteforce"
	"zconect/server/internal/config"
	"zconect/server/internal/logging"
	"zconect/server/internal/ratelimit"
	"zconect/server/internal/admin"
	"zconect/server/internal/session"
	"zconect/server/internal/signaling"
	"zconect/server/internal/telemetry"
	"zconect/server/internal/update"
)

func main() {
	cfg := config.Load()

	logger, err := logging.New("logs.log")
	if err != nil {
		panic(err)
	}
	defer logger.Close()

	svc := session.NewService(cfg.SessionTTL, cfg.MaxJoinAttempts, cfg.LockDuration)
	tokenSvc := auth.NewTokenService(60 * time.Second)
	handler := api.NewHandler(svc, logger, tokenSvc)
	// F-07 audit 2026-04-24: X-Forwarded-For handling. Без этого audit log + admin panel
	// видят только Caddy's docker IP (172.18.0.4) вместо real client IP. TRUSTED_PROXIES
	// в .env ожидает CIDR "172.16.0.0/12" для всего Docker bridge range.
	handler.SetTrustedProxies(cfg.TrustedProxies)

	// Per-IP enumeration tracker (observability only, не автобан).
	// Счётчик любых session_join_failed за 5-мин окно → показывает admin'у
	// в /admin/api/suspicious-ips чтобы он мог ручной ban при необходимости.
	ipTracker := bruteforce.NewIPTracker(5 * time.Minute)
	ipTracker.StartSweeper(5 * time.Minute)
	handler.SetJoinFailureRecorder(ipTracker.RecordFailure)
	hub := signaling.NewHub()
	wsHandler := signaling.NewHandlerWithEnv(hub, svc, logger, tokenSvc, cfg.AllowedOrigins, cfg.AppEnv)

	// Telemetry: SQLite store + GeoIP resolver + dashboard.
	telStore, err := telemetry.NewStore(cfg.TelemetryDBPath)
	if err != nil {
		logger.Log("ERROR", "Main", "telemetry_db_failed", "failed to open telemetry DB", logging.Entry{Error: err.Error()})
		panic(err)
	}
	defer telStore.Close()
	geoResolver := telemetry.NewGeoResolver()
	telHandler := telemetry.NewHandler(telStore, svc, hub, geoResolver, logger, cfg.DashboardAPIKey, cfg.TrustedProxies)

	mux := http.NewServeMux()
	handler.Register(mux)
	telHandler.Register(mux)
	mux.Handle("/ws", wsHandler)

	// Auto-update: /api/v1/client/version serves latest version metadata.
	if cfg.ClientVersionFile != "" {
		updateHandler := update.NewHandler(cfg.ClientVersionFile, logger)
		updateHandler.Register(mux)
		logger.Log("INFO", "Main", "update_handler_registered", "auto-update endpoint registered path="+cfg.ClientVersionFile, logging.Entry{})
	}

	// Admin panel (session-based auth, /admin/*). Enabled only when
	// ADMIN_PASSWORD_HASH is set. SSH into server + admin-hashpass CLI to generate.
	adminHandler, err := admin.NewHandler(admin.Config{
		PasswordHash:      cfg.AdminPasswordHash,
		PasswordHashFile:  cfg.AdminPasswordHashFile,
		AllowedIPs:        cfg.AdminAllowedIPs,
		SessionIdleTTL:    cfg.AdminSessionIdleTTL,
		SessionMaxLife:    cfg.AdminSessionMaxLife,
		AuditLogPath:      cfg.AdminAuditLog,
		AuditLogMaxMB:     cfg.AdminAuditLogMaxMB,
		AuditLogKeep:      cfg.AdminAuditLogKeep,
		ClientVersionFile: cfg.ClientVersionFile,
		TelemetryDBPath:   cfg.TelemetryDBPath,
		TrustedProxies:    cfg.TrustedProxies,
		CookieSecure:      cfg.AdminCookieSecure,
	}, admin.Services{
		Sessions:       svc,
		Peers:          hub,
		WebRTCSessions: sessionsAdapter{svc: svc},
		WebRTCKiller:   killerAdapter{svc: svc, hub: hub},
		SuspiciousIPs:  suspiciousAdapter{tracker: ipTracker},
	}, logger)
	if err != nil {
		logger.Log("ERROR", "Main", "admin_init_failed", "admin handler init failed", logging.Entry{Error: err.Error()})
		panic(err)
	}
	adminHandler.Register(mux)
	defer adminHandler.Shutdown()

	// Wire maintenance + ban checks в API handler.
	// api.Handler проверяет эти функции при каждом /api/v1/session/create и /join.
	handler.SetMaintenanceCheck(adminHandler.IsMaintenance)
	handler.SetBannedIPCheck(admin.IsIPBanned)

	// TURN rotating credentials — inject shared secret + public TURN addr.
	// Если secret пустой, клиент fallback на static config (legacy mode).
	turnPublicAddr := cfg.TurnPublicAddr()
	if cfg.TurnAuthSecret != "" && turnPublicAddr != "" {
		handler.SetTurnAuth(cfg.TurnAuthSecret, cfg.TurnCredTTL, turnPublicAddr, cfg.TurnRealm)
		logger.Log("INFO", "Main", "turn_rotating_creds_enabled",
			"rotating TURN credentials enabled — clients получат signed creds в session responses",
			logging.Entry{})
	} else if cfg.TurnAuthSecret == "" {
		logger.Log("WARN", "Main", "turn_rotating_creds_disabled",
			"TURN_AUTH_SECRET не задан — rotating TURN credentials отключены, клиенты используют static config",
			logging.Entry{})
	}

	// Permit admin session to access /api/v1/telemetry/stats (без DASHBOARD_API_KEY).
	// Dashboard UI fetch'ит его напрямую; admin cookie достаточно для auth.
	telHandler.SetAdminAuthCheck(adminHandler.IsAuthenticated)

	// Per-IP rate limiting for API endpoints.
	limiter := ratelimit.New(cfg.RateLimit, time.Minute, cfg.TrustedProxies)

	// F-08 fix: warn if ALLOWED_ORIGINS is empty in production.
	if cfg.AppEnv == "prod" && len(cfg.AllowedOrigins) == 0 {
		logger.Log("WARN", "Main", "no_allowed_origins", "ALLOWED_ORIGINS is empty in production — WS connections with Origin header will be denied", logging.Entry{})
	}

	server := &http.Server{
		Addr:              ":" + cfg.Port,
		Handler:           limiter.Middleware(mux),
		ReadHeaderTimeout: 5 * time.Second,
	}

	go pruneLoop(svc, logger)

	go func() {
		logger.Log("INFO", "Main", "server_start", "signaling server started", logging.Entry{})
		if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			logger.Log("ERROR", "Main", "server_crash", "server crashed", logging.Entry{Error: err.Error()})
		}
	}()

	stop := make(chan os.Signal, 1)
	signal.Notify(stop, syscall.SIGINT, syscall.SIGTERM)
	<-stop

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	logger.Log("INFO", "Main", "server_shutdown", "graceful shutdown started", logging.Entry{})
	_ = server.Shutdown(ctx)
	logger.Log("INFO", "Main", "server_shutdown_done", "graceful shutdown finished", logging.Entry{})
}

// sessionsAdapter — adapter session.Service → admin.WebRTCSessionsProvider.
// Оборачивает ListActive() в срез admin.WebRTCSessionInfo (без зависимости admin
// на session package).
type sessionsAdapter struct {
	svc *session.Service
}

func (a sessionsAdapter) ListActive() []admin.WebRTCSessionInfo {
	active := a.svc.ListActive()
	result := make([]admin.WebRTCSessionInfo, 0, len(active))
	for _, s := range active {
		info := admin.WebRTCSessionInfo{
			ID:                  s.ID,
			LoginCode:           s.LoginCode,
			State:               string(s.State),
			CreatedAt:           s.CreatedAt,
			ExpiresAt:           s.ExpiresAt,
			JoinAttempts:        s.JoinAttempts,
			TotalFailedAttempts: s.TotalFailedAttempts,
			LockoutTier:         s.LockoutTier,
			// PeerCount читается из hub.PeerCountInSession если hub даёт API
		}
		if !s.LockedUntil.IsZero() {
			info.LockedUntilUnixSec = s.LockedUntil.Unix()
		}
		result = append(result, info)
	}
	return result
}

// killerAdapter — закрытие session со стороны admin: mark closed в service
// + drop WS peers из hub для немедленного эффекта.
type killerAdapter struct {
	svc *session.Service
	hub *signaling.Hub
}

func (a killerAdapter) KillSession(sessionID string) bool {
	ok := a.svc.KillSession(sessionID)
	// TODO: hub.DropAllPeersInSession(sessionID) — требует добавления метода в hub
	return ok
}

// suspiciousAdapter — маппит bruteforce.IPTracker в admin.SuspiciousIPProvider
// с конверсией IPStat → admin.SuspiciousIP (избегаем cross-package type dependency).
type suspiciousAdapter struct {
	tracker *bruteforce.IPTracker
}

func (a suspiciousAdapter) Snapshot(minCount int) []admin.SuspiciousIP {
	src := a.tracker.Snapshot(minCount)
	out := make([]admin.SuspiciousIP, 0, len(src))
	for _, s := range src {
		out = append(out, admin.SuspiciousIP{
			IP:        s.IP,
			Count:     s.Count,
			FirstSeen: s.FirstSeen,
			LastSeen:  s.LastSeen,
		})
	}
	return out
}
func (a suspiciousAdapter) WindowSec() int { return a.tracker.WindowSec() }

func pruneLoop(svc *session.Service, logger *logging.Logger) {
	ticker := time.NewTicker(30 * time.Second)
	defer ticker.Stop()
	for range ticker.C {
		removed := svc.PruneExpired(time.Now().UTC())
		if removed > 0 {
			logger.Log("INFO", "Session", "session_pruned", "expired sessions removed count="+strconv.Itoa(removed), logging.Entry{})
		}
	}
}
