package api

import (
	"encoding/json"
	"errors"
	"net"
	"net/http"
	"time"

	"zconect/server/internal/logging"
	"zconect/server/internal/session"
)

type Handler struct {
	sessions *session.Service
	logger   *logging.Logger
}

func NewHandler(sessions *session.Service, logger *logging.Logger) *Handler {
	return &Handler{
		sessions: sessions,
		logger:   logger,
	}
}

func (h *Handler) Register(mux *http.ServeMux) {
	mux.HandleFunc("/healthz", h.health)
	mux.HandleFunc("/api/v1/session/create", h.createSession)
	mux.HandleFunc("/api/v1/session/join", h.joinSession)
	mux.HandleFunc("/api/v1/session/close", h.closeSession)
}

type createSessionRequest struct {
	RequestUnattended bool `json:"request_unattended"`
}

type createSessionResponse struct {
	SessionID    string `json:"session_id"`
	LoginCode    string `json:"login_code"`
	PassCode     string `json:"pass_code"`
	ExpiresInSec int    `json:"expires_in_sec"`
	WSURL        string `json:"ws_url"`
	WSToken      string `json:"ws_token"`
}

func (h *Handler) createSession(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeJSON(w, http.StatusMethodNotAllowed, map[string]string{"error": "method not allowed"})
		return
	}

	var req createSessionRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid json"})
		return
	}

	sess, err := h.sessions.Create(!req.RequestUnattended)
	if err != nil {
		h.logger.Log("ERROR", "API", "session_create_failed", "failed to create session", logging.Entry{Error: err.Error(), IP: clientIP(r)})
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "internal error"})
		return
	}

	expiresIn := int(time.Until(sess.ExpiresAt).Seconds())
	if expiresIn < 0 {
		expiresIn = 0
	}

	h.logger.Log("INFO", "API", "session_created", "session created", logging.Entry{SessionID: sess.ID, IP: clientIP(r)})
	writeJSON(w, http.StatusOK, createSessionResponse{
		SessionID:    sess.ID,
		LoginCode:    sess.LoginCode,
		PassCode:     sess.PassCode,
		ExpiresInSec: expiresIn,
		WSURL:        "/ws",
		WSToken:      "todo-short-lived-token",
	})
}

type joinSessionRequest struct {
	LoginCode string `json:"login_code"`
	PassCode  string `json:"pass_code"`
}

type joinSessionResponse struct {
	SessionID      string `json:"session_id"`
	RequireConfirm bool   `json:"require_confirm"`
	State          string `json:"state"`
	WSURL          string `json:"ws_url"`
	WSToken        string `json:"ws_token"`
}

func (h *Handler) joinSession(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeJSON(w, http.StatusMethodNotAllowed, map[string]string{"error": "method not allowed"})
		return
	}

	var req joinSessionRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid json"})
		return
	}
	if len(req.LoginCode) != 8 || len(req.PassCode) != 8 {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "login_code and pass_code must be 8 digits"})
		return
	}

	sess, err := h.sessions.Join(req.LoginCode, req.PassCode)
	if err != nil {
		status := http.StatusUnauthorized
		msg := "join rejected"
		switch {
		case errors.Is(err, session.ErrExpired):
			status = http.StatusGone
			msg = "session expired"
		case errors.Is(err, session.ErrLocked):
			status = http.StatusTooManyRequests
			msg = "session locked"
		}
		h.logger.Log("WARN", "API", "session_join_failed", msg, logging.Entry{Error: err.Error(), IP: clientIP(r)})
		writeJSON(w, status, map[string]string{"error": msg})
		return
	}

	h.logger.Log("INFO", "API", "session_joined", "session joined", logging.Entry{SessionID: sess.ID, IP: clientIP(r)})
	writeJSON(w, http.StatusOK, joinSessionResponse{
		SessionID:      sess.ID,
		RequireConfirm: sess.RequireConfirm,
		State:          string(sess.State),
		WSURL:          "/ws",
		WSToken:        "todo-short-lived-token",
	})
}

type closeSessionRequest struct {
	SessionID string `json:"session_id"`
}

func (h *Handler) closeSession(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeJSON(w, http.StatusMethodNotAllowed, map[string]string{"error": "method not allowed"})
		return
	}

	var req closeSessionRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid json"})
		return
	}
	if req.SessionID == "" {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "session_id is required"})
		return
	}

	if err := h.sessions.Close(req.SessionID); err != nil {
		h.logger.Log("WARN", "API", "session_close_failed", "session close failed", logging.Entry{Error: err.Error(), IP: clientIP(r)})
		writeJSON(w, http.StatusNotFound, map[string]string{"error": "session not found"})
		return
	}

	h.logger.Log("INFO", "API", "session_closed", "session closed", logging.Entry{SessionID: req.SessionID, IP: clientIP(r)})
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

func (h *Handler) health(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

func writeJSON(w http.ResponseWriter, status int, data any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(data)
}

func clientIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}
