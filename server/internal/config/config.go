package config

import (
	"os"
	"strconv"
	"time"
)

type Config struct {
	Port           string
	SessionTTL     time.Duration
	MaxJoinAttempts int
	LockDuration   time.Duration
}

func Load() Config {
	return Config{
		Port:            getenv("SIGNALING_PORT", "8080"),
		SessionTTL:      time.Duration(getenvInt("SESSION_TTL_SEC", 300)) * time.Second,
		MaxJoinAttempts: getenvInt("MAX_JOIN_ATTEMPTS", 5),
		LockDuration:    time.Duration(getenvInt("LOCK_MINUTES", 10)) * time.Minute,
	}
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
