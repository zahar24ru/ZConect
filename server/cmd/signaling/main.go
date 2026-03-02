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
	"zconect/server/internal/config"
	"zconect/server/internal/logging"
	"zconect/server/internal/session"
	"zconect/server/internal/signaling"
)

func main() {
	cfg := config.Load()

	logger, err := logging.New("logs.log")
	if err != nil {
		panic(err)
	}
	defer logger.Close()

	svc := session.NewService(cfg.SessionTTL, cfg.MaxJoinAttempts, cfg.LockDuration)
	handler := api.NewHandler(svc, logger)
	hub := signaling.NewHub()
	wsHandler := signaling.NewHandler(hub, svc, logger)

	mux := http.NewServeMux()
	handler.Register(mux)
	mux.Handle("/ws", wsHandler)

	server := &http.Server{
		Addr:              ":" + cfg.Port,
		Handler:           mux,
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
