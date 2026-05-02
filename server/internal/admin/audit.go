package admin

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

// AuditLog — append-only log for admin actions. NEVER logs secrets (пароли,
// session tokens, CSRF tokens). Only: timestamp, IP, action, relevant metadata.
//
// Format: one JSON object per line (NDJSON). Easy to grep / parse.
//
// Retention: auto-rotate при достижении max size (default 10 MB).
// Старые файлы переименовываются в `<path>.1`, `<path>.2`, …, до KeepCount штук.
// Настраивается через:
//   - ADMIN_AUDIT_LOG       — path (default admin-audit.log)
//   - ADMIN_AUDIT_LOG_MAX_MB — max size before rotate (default 10)
//   - ADMIN_AUDIT_LOG_KEEP   — how many old files to keep (default 5)
type AuditLog struct {
	mu         sync.Mutex
	path       string
	f          *os.File
	maxBytes   int64 // rotate threshold
	keepCount  int   // how many old files to keep
	writtenSize int64 // current file size (updated on every write)
}

// AuditLogConfig — параметры retention. Nil defaults → 10 MB, 5 old files.
type AuditLogConfig struct {
	MaxMB     int // per-file max size, default 10
	KeepCount int // old files to keep, default 5
}

func NewAuditLog(path string) (*AuditLog, error) {
	return NewAuditLogWithConfig(path, AuditLogConfig{})
}

func NewAuditLogWithConfig(path string, cfg AuditLogConfig) (*AuditLog, error) {
	if path == "" {
		return &AuditLog{}, nil // noop mode
	}
	maxMB := cfg.MaxMB
	if maxMB <= 0 {
		maxMB = 10
	}
	keep := cfg.KeepCount
	if keep <= 0 {
		keep = 5
	}
	// Ensure directory exists
	if dir := filepath.Dir(path); dir != "" && dir != "." {
		_ = os.MkdirAll(dir, 0o755)
	}
	f, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o600)
	if err != nil {
		return nil, fmt.Errorf("open audit log: %w", err)
	}
	// Get current size (если файл уже существовал).
	var size int64
	if info, err := f.Stat(); err == nil {
		size = info.Size()
	}
	return &AuditLog{
		path:        path,
		f:           f,
		maxBytes:    int64(maxMB) * 1024 * 1024,
		keepCount:   keep,
		writtenSize: size,
	}, nil
}

// Entry — structure of one audit record. Extend as needed — fields с `omitempty`
// не появляются в JSON если пусты, чтобы файл оставался compact.
type Entry struct {
	Timestamp string            `json:"ts"`
	IP        string            `json:"ip"`
	Action    string            `json:"action"`
	Result    string            `json:"result,omitempty"` // "ok" / "fail" / "denied"
	Reason    string            `json:"reason,omitempty"`
	Metadata  map[string]string `json:"meta,omitempty"`
}

// Log — atomically append one entry. Non-fatal on error (return nil) —
// audit log failure shouldn't break admin flow.
func (a *AuditLog) Log(entry Entry) {
	if a == nil || a.f == nil {
		return
	}
	if entry.Timestamp == "" {
		entry.Timestamp = time.Now().UTC().Format(time.RFC3339Nano)
	}
	raw, err := json.Marshal(entry)
	if err != nil {
		return
	}
	a.mu.Lock()
	defer a.mu.Unlock()

	// Check size BEFORE write — rotate if threshold exceeded.
	if a.maxBytes > 0 && a.writtenSize+int64(len(raw))+1 > a.maxBytes {
		if err := a.rotateLocked(); err != nil {
			// Log rotation failed — не hang application, продолжаем писать в
			// old file. Через некоторое время попытка rotation повторится.
			// (Could leak fd but не блокирующий).
			_ = err
		}
	}

	n1, _ := a.f.Write(raw)
	n2, _ := a.f.Write([]byte("\n"))
	a.writtenSize += int64(n1 + n2)
}

// rotateLocked — вызывается под a.mu.Lock(). Переименовывает current file в
// .1, старые .1→.2, .2→.3, .N-1→.N, старейший (.N) удаляется. Открывает fresh file.
func (a *AuditLog) rotateLocked() error {
	if a.f == nil {
		return nil
	}
	// Close current file descriptor чтобы rename worked на Windows.
	if err := a.f.Close(); err != nil {
		return fmt.Errorf("close old file: %w", err)
	}
	a.f = nil

	// Shift old files: .4 → .5 (если keep=5), .3 → .4, …, .1 → .2
	// Oldest gets overwritten/removed.
	for i := a.keepCount - 1; i >= 1; i-- {
		oldPath := fmt.Sprintf("%s.%d", a.path, i)
		newPath := fmt.Sprintf("%s.%d", a.path, i+1)
		if _, err := os.Stat(oldPath); err == nil {
			// If destination exists — remove (handles keep=1 edge case + overwrite).
			if i+1 > a.keepCount {
				_ = os.Remove(oldPath)
			} else {
				_ = os.Remove(newPath) // remove existing destination (Windows rename fails if target exists)
				_ = os.Rename(oldPath, newPath)
			}
		}
	}
	// Rename current file to .1.
	rotated := a.path + ".1"
	_ = os.Remove(rotated) // clear anything that might be there
	if err := os.Rename(a.path, rotated); err != nil {
		// Rename failed — попытаемся просто открыть заново (это продолжает writing).
		// Старые записи остаются в файле, будет попытка rotation снова позже.
	}

	// Open fresh file.
	f, err := os.OpenFile(a.path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o600)
	if err != nil {
		return fmt.Errorf("reopen after rotate: %w", err)
	}
	a.f = f
	a.writtenSize = 0
	return nil
}

// ListRotatedFiles — returns list of rotated file paths (oldest first).
// Полезно для admin panel чтобы показать available backups.
func (a *AuditLog) ListRotatedFiles() []string {
	if a == nil || a.path == "" {
		return nil
	}
	files := make([]string, 0, a.keepCount)
	for i := 1; i <= a.keepCount; i++ {
		p := fmt.Sprintf("%s.%d", a.path, i)
		if _, err := os.Stat(p); err == nil {
			files = append(files, p)
		}
	}
	// Sort by suffix number — .1 newest, .N oldest. We want oldest first.
	sort.Slice(files, func(i, j int) bool {
		return extractRotateIdx(files[i]) > extractRotateIdx(files[j])
	})
	return files
}

func extractRotateIdx(path string) int {
	// Expect path like "admin-audit.log.3" → extract 3.
	idx := strings.LastIndex(path, ".")
	if idx == -1 {
		return 0
	}
	var n int
	if _, err := fmt.Sscanf(path[idx+1:], "%d", &n); err != nil {
		return 0
	}
	return n
}

// Close — flush + close. Call on graceful shutdown.
func (a *AuditLog) Close() error {
	if a == nil || a.f == nil {
		return nil
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.f.Close()
}
