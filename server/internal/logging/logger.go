package logging

import (
	"encoding/json"
	"os"
	"sync"
	"time"
)

type Logger struct {
	mu   sync.Mutex
	file *os.File
}

type Entry struct {
	TS        string `json:"ts"`
	Level     string `json:"level"`
	Module    string `json:"module"`
	Event     string `json:"event"`
	Message   string `json:"message"`
	SessionID string `json:"session_id,omitempty"`
	Error     string `json:"error,omitempty"`
	IP        string `json:"ip,omitempty"`
}

func New(path string) (*Logger, error) {
	f, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return nil, err
	}
	return &Logger{file: f}, nil
}

func (l *Logger) Close() error {
	return l.file.Close()
}

func (l *Logger) Log(level, module, event, message string, extras Entry) {
	entry := Entry{
		TS:      time.Now().UTC().Format(time.RFC3339Nano),
		Level:   level,
		Module:  module,
		Event:   event,
		Message: message,
	}
	entry.SessionID = extras.SessionID
	entry.Error = extras.Error
	entry.IP = extras.IP

	raw, err := json.Marshal(entry)
	if err != nil {
		return
	}

	l.mu.Lock()
	defer l.mu.Unlock()
	_, _ = l.file.Write(append(raw, '\n'))
}
