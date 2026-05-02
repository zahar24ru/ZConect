package telemetry

import (
	_ "embed"
	"encoding/json"
	"net/http"

	"zconect/server/internal/logging"
	"zconect/server/internal/realip"
)

//go:embed dashboard.html
var dashboardHTML []byte

// SessionCounter provides active session count (implemented by session.Service).
type SessionCounter interface {
	ActiveCount() int
}

// PeerCounter provides active peer/room counts (implemented by signaling.Hub).
type PeerCounter interface {
	RoomCount() int
	PeerCount() int
}

// Handler serves telemetry endpoints.
type Handler struct {
	store          *Store
	sessions       SessionCounter
	peers          PeerCounter
	geo            *GeoResolver
	logger         *logging.Logger
	apiKey string
	realIP *realip.Extractor

	// adminAuthCheck — опциональный callback. Если set и возвращает true,
	// запрос проходит auth (используется для admin panel cookie-based access).
	// main.go wiring: telHandler.SetAdminAuthCheck(adminHandler.IsAuthenticated).
	adminAuthCheck func(r *http.Request) bool
}

// SetAdminAuthCheck — инжектится из main.go после создания admin handler'а,
// чтобы telemetry не импортировал admin package (избегаем circular).
func (h *Handler) SetAdminAuthCheck(f func(r *http.Request) bool) {
	h.adminAuthCheck = f
}

// NewHandler creates a telemetry handler.
func NewHandler(store *Store, sessions SessionCounter, peers PeerCounter, geo *GeoResolver, logger *logging.Logger, apiKey string, trustedProxies []string) *Handler {
	return &Handler{
		store:    store,
		sessions: sessions,
		peers:    peers,
		geo:      geo,
		logger:   logger,
		apiKey:   apiKey,
		realIP:   realip.New(trustedProxies),
	}
}

// Register adds telemetry routes to the mux.
//
// NOTE (2026-04-21): /dashboard removed — заменён на /admin panel с session auth.
// Legacy dashboard HTML кладётся в admin package now. dashboard() func оставлена
// как dead code на случай rollback, но не register'ится.
// /api/v1/telemetry/stats остаётся — используется admin dashboard'ом для данных.
func (h *Handler) Register(mux *http.ServeMux) {
	mux.HandleFunc("/api/v1/telemetry/heartbeat", h.heartbeat)
	mux.HandleFunc("/api/v1/telemetry/stats", h.stats)
}

type heartbeatRequest struct {
	MachineID   string `json:"machine_id"`
	AppVersion  string `json:"app_version"`
	OSVersion   string `json:"os_version"`
	OSLanguage  string `json:"os_language"`
	Platform    string `json:"platform"`     // optional: "windows" | "android"
	DeviceModel string `json:"device_model"` // optional: Android device model
}

func (h *Handler) heartbeat(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, 4096)
	var req heartbeatRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, "invalid json", http.StatusBadRequest)
		return
	}
	if req.MachineID == "" {
		http.Error(w, "machine_id required", http.StatusBadRequest)
		return
	}

	ip := h.clientIP(r)
	country := h.geo.Resolve(ip)

	if err := h.store.Heartbeat(r.Context(), req.MachineID, req.OSVersion, req.OSLanguage, req.AppVersion, country, ip, req.Platform, req.DeviceModel); err != nil {
		h.logger.Log("ERROR", "Telemetry", "heartbeat_failed", err.Error(), logging.Entry{IP: ip})
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	w.Write([]byte(`{"ok":true}`))
}

type statsResponse struct {
	OnlineCount              int         `json:"online_count"`
	ActiveSessions           int         `json:"active_sessions"`
	ActivePeers              int         `json:"active_peers"`
	TotalInstalled           int         `json:"total_installed"`
	OSDistribution           []DistEntry `json:"os_distribution"`
	LanguageDistribution     []DistEntry `json:"language_distribution"`
	CountryDistribution      []DistEntry `json:"country_distribution"`
	VersionDistribution      []DistEntry `json:"version_distribution"`
	DeviceModelDistribution  []DistEntry `json:"device_model_distribution,omitempty"`
	RecentDevices            []Device    `json:"recent_devices"`
	Platform                 string      `json:"platform,omitempty"` // echo of filter
}

func (h *Handler) stats(w http.ResponseWriter, r *http.Request) {
	if !h.checkAPIKey(w, r) {
		return
	}

	// Optional filter: ?platform=windows|android (empty = all)
	platform := r.URL.Query().Get("platform")
	if platform != "" && platform != "windows" && platform != "android" {
		http.Error(w, "invalid platform", http.StatusBadRequest)
		return
	}

	ctx := r.Context()
	online, _ := h.store.OnlineCount(ctx, platform)
	total, _ := h.store.TotalInstalled(ctx, platform)
	osDist, _ := h.store.OSDistribution(ctx, platform)
	langDist, _ := h.store.LanguageDistribution(ctx, platform)
	countryDist, _ := h.store.CountryDistribution(ctx, platform)
	versionDist, _ := h.store.VersionDistribution(ctx, platform)
	recent, _ := h.store.RecentDevices(ctx, 20, platform)

	resp := statsResponse{
		OnlineCount:          online,
		ActiveSessions:       h.sessions.ActiveCount(),
		ActivePeers:          h.peers.PeerCount(),
		TotalInstalled:       total,
		OSDistribution:       osDist,
		LanguageDistribution: langDist,
		CountryDistribution:  countryDist,
		VersionDistribution:  versionDist,
		RecentDevices:        recent,
		Platform:             platform,
	}

	// Device models are only meaningful for Android
	if platform == "android" || platform == "" {
		models, _ := h.store.DeviceModelDistribution(ctx, platform)
		resp.DeviceModelDistribution = models
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(resp)
}

func (h *Handler) dashboard(w http.ResponseWriter, r *http.Request) {
	if !h.checkAPIKey(w, r) {
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Write(dashboardHTML)
}

func (h *Handler) checkAPIKey(w http.ResponseWriter, r *http.Request) bool {
	// Admin cookie — если задан callback и он сказал "ок", сразу пропускаем.
	// Это нужно чтобы dashboard fetch'ил /api/v1/telemetry/stats без API key —
	// admin session достаточно.
	if h.adminAuthCheck != nil && h.adminAuthCheck(r) {
		return true
	}
	// Legacy: DASHBOARD_API_KEY в query. Оставлено для back-compat (например
	// monitoring scripts). Новые users должны использовать admin panel.
	if h.apiKey == "" {
		http.Error(w, "unauthorized (admin session required)", http.StatusUnauthorized)
		return false
	}
	key := r.URL.Query().Get("key")
	if key != h.apiKey {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return false
	}
	return true
}

func (h *Handler) clientIP(r *http.Request) string {
	return h.realIP.Extract(r)
}
