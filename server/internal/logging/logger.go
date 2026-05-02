package logging

import (
	"encoding/json"
	"fmt"
	"os"
	"sync"
	"time"
)

const (
	maxLogSize = 5 * 1024 * 1024 // 5 MB
	maxBackups = 3
)

type Logger struct {
	mu   sync.Mutex
	file *os.File
	path string
	size int64
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
	fi, _ := f.Stat()
	var sz int64
	if fi != nil {
		sz = fi.Size()
	}
	return &Logger{file: f, path: path, size: sz}, nil
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
	line := append(raw, '\n')

	l.mu.Lock()
	defer l.mu.Unlock()
	n, _ := l.file.Write(line)
	l.size += int64(n)
	l.rotateIfNeeded()
}

// N7-09: rotate log files when size exceeds maxLogSize.
func (l *Logger) rotateIfNeeded() {
	if l.size < maxLogSize {
		return
	}
	_ = l.file.Close()

	// Shift backups: logs.3.log → delete, logs.2.log → logs.3.log, etc.
	for i := maxBackups; i >= 1; i-- {
		src := fmt.Sprintf("%s.%d", l.path, i)
		if i == maxBackups {
			_ = os.Remove(src)
		} else {
			dst := fmt.Sprintf("%s.%d", l.path, i+1)
			_ = os.Rename(src, dst)
		}
	}
	_ = os.Rename(l.path, l.path+".1")

	f, err := os.OpenFile(l.path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return
	}
	l.file = f
	l.size = 0
}
