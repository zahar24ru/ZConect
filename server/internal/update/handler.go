// Package update serves client version metadata for auto-update flow.
//
// The endpoint GET /api/v1/client/version returns the latest available
// client version + download URL. Clients poll this on startup (and
// periodically) to notify users about available updates.
//
// Version metadata is stored in a JSON file (client-version.json) so it
// can be updated without recompiling the server. File path is configured
// via CLIENT_VERSION_FILE env var; default is ./client-version.json next
// to the server binary.
package update

import (
	"encoding/json"
	"net/http"
	"os"
	"sync"
	"time"

	"zconect/server/internal/logging"
)

// VersionInfo is returned to client — all fields are public metadata.
type VersionInfo struct {
	LatestVersion        string `json:"latest_version"`         // "1.0.1"
	DownloadURL          string `json:"download_url"`           // HTTPS URL к installer'у
	ReleaseNotes         string `json:"release_notes"`          // short changelog
	MinCompatibleVersion string `json:"min_compatible_version"` // если client < min, force update
	PublishedAt          string `json:"published_at,omitempty"` // ISO 8601 date
	SHA256               string `json:"sha256,omitempty"`       // 64 hex chars; client verifies installer file integrity
}

// Handler serves version metadata. Safe for concurrent use.
// Re-reads the version file every 30s (cheap, tiny JSON) so ops can
// push new versions without server restart.
type Handler struct {
	filePath string
	logger   *logging.Logger

	mu         sync.RWMutex
	cached     *VersionInfo
	cachedAt   time.Time
	cacheTTL   time.Duration
	lastErrLog time.Time
}

func NewHandler(filePath string, logger *logging.Logger) *Handler {
	return &Handler{
		filePath: filePath,
		logger:   logger,
		cacheTTL: 30 * time.Second,
	}
}

func (h *Handler) Register(mux *http.ServeMux) {
	mux.HandleFunc("/api/v1/client/version", h.getVersion)
}

func (h *Handler) getVersion(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		w.Header().Set("Allow", "GET")
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	info, err := h.loadCached()
	if err != nil {
		// Don't spam the log if file is missing — log once per minute at most.
		h.mu.Lock()
		if time.Since(h.lastErrLog) > time.Minute {
			h.logger.Log("WARN", "Update", "version_file_load_failed",
				"client-version.json not found or unreadable — returning 503",
				logging.Entry{Error: err.Error()})
			h.lastErrLog = time.Now()
		}
		h.mu.Unlock()
		http.Error(w, "version info unavailable", http.StatusServiceUnavailable)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "public, max-age=30")
	_ = json.NewEncoder(w).Encode(info)
}

func (h *Handler) loadCached() (*VersionInfo, error) {
	h.mu.RLock()
	if h.cached != nil && time.Since(h.cachedAt) < h.cacheTTL {
		cached := *h.cached
		h.mu.RUnlock()
		return &cached, nil
	}
	h.mu.RUnlock()

	// Cache miss or expired — re-read.
	h.mu.Lock()
	defer h.mu.Unlock()
	// Double-check — another goroutine may have already loaded.
	if h.cached != nil && time.Since(h.cachedAt) < h.cacheTTL {
		cached := *h.cached
		return &cached, nil
	}

	raw, err := os.ReadFile(h.filePath)
	if err != nil {
		return nil, err
	}
	var info VersionInfo
	if err := json.Unmarshal(raw, &info); err != nil {
		return nil, err
	}
	h.cached = &info
	h.cachedAt = time.Now()
	cached := info
	return &cached, nil
}
