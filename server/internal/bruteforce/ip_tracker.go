// Package bruteforce — per-IP sliding window tracker для session_join_failed events.
//
// Purpose: обнаружение enumeration attacks когда attacker пробует НЕсуществующие
// login_codes. Per-session brute-force protection (session.service.go) в таких
// случаях не работает — Join возвращает ErrBadCredentials до достижения любой
// сессии, session-level counter не инкрементируется.
//
// Observability only — auto-ban НЕ делается (user решил manual only через
// admin UI ban list для agressive cases). Просто показывает admin'у "IP X
// сделал N неуспешных /join за окно" — а он уже решает банить или нет.
//
// Design:
//   - Per-IP slice of timestamps; pruned по окну на каждой записи
//   - Memory-bounded: cap per-IP history, periodic sweeper по всей map
//   - Thread-safe через один mutex (простой случай, не lock-free нужен)
package bruteforce

import (
	"sort"
	"sync"
	"time"
)

type IPTracker struct {
	mu       sync.Mutex
	window   time.Duration
	failures map[string][]time.Time
	// maxPerIP — cap slice length чтобы attacker'у не выгодно спамить
	// (всё равно отображается как "очень подозрительный" после threshold).
	maxPerIP int
}

// NewIPTracker — window = окно подсчёта (обычно 5-15 минут).
// Default threshold для visibility — 5 failures означают "подозрительный".
func NewIPTracker(window time.Duration) *IPTracker {
	if window <= 0 {
		window = 5 * time.Minute
	}
	return &IPTracker{
		window:   window,
		failures: make(map[string][]time.Time),
		maxPerIP: 100,
	}
}

// RecordFailure — инкрементирует счётчик для IP. Empty IP игнорируется.
func (t *IPTracker) RecordFailure(ip string) {
	if ip == "" {
		return
	}
	now := time.Now().UTC()
	cutoff := now.Add(-t.window)

	t.mu.Lock()
	defer t.mu.Unlock()

	times := t.failures[ip]
	pruned := times[:0]
	for _, ts := range times {
		if ts.After(cutoff) {
			pruned = append(pruned, ts)
		}
	}
	pruned = append(pruned, now)
	if len(pruned) > t.maxPerIP {
		pruned = pruned[len(pruned)-t.maxPerIP:]
	}
	t.failures[ip] = pruned
}

// IPStat — snapshot одного IP для admin panel.
type IPStat struct {
	IP        string    `json:"ip"`
	Count     int       `json:"count"`
	FirstSeen time.Time `json:"first_seen"`
	LastSeen  time.Time `json:"last_seen"`
}

// Snapshot — все IP с >=minCount failures за окно, sorted DESC by Count.
// minCount=1 вернёт всех с failures, minCount=5 — только подозрительных.
func (t *IPTracker) Snapshot(minCount int) []IPStat {
	if minCount < 1 {
		minCount = 1
	}
	now := time.Now().UTC()
	cutoff := now.Add(-t.window)

	t.mu.Lock()
	defer t.mu.Unlock()

	out := make([]IPStat, 0, len(t.failures))
	for ip, times := range t.failures {
		var first, last time.Time
		count := 0
		for _, ts := range times {
			if ts.After(cutoff) {
				if count == 0 || ts.Before(first) {
					first = ts
				}
				if ts.After(last) {
					last = ts
				}
				count++
			}
		}
		if count >= minCount {
			out = append(out, IPStat{
				IP: ip, Count: count,
				FirstSeen: first, LastSeen: last,
			})
		}
	}
	// Stable sort DESC by Count, tie-break LastSeen DESC (самые свежие сверху).
	sort.SliceStable(out, func(i, j int) bool {
		if out[i].Count != out[j].Count {
			return out[i].Count > out[j].Count
		}
		return out[i].LastSeen.After(out[j].LastSeen)
	})
	return out
}

// StartSweeper — periodic cleanup IPs без recent failures.
// Не критичен для correctness (Snapshot сам окно учитывает), но предотвращает
// unbounded map growth при большом потоке уникальных IPs (mobile carrier NAT etc.).
func (t *IPTracker) StartSweeper(interval time.Duration) {
	if interval <= 0 {
		interval = 5 * time.Minute
	}
	go func() {
		ticker := time.NewTicker(interval)
		defer ticker.Stop()
		for range ticker.C {
			now := time.Now().UTC()
			cutoff := now.Add(-t.window)
			t.mu.Lock()
			for ip, times := range t.failures {
				hasRecent := false
				for _, ts := range times {
					if ts.After(cutoff) {
						hasRecent = true
						break
					}
				}
				if !hasRecent {
					delete(t.failures, ip)
				}
			}
			t.mu.Unlock()
		}
	}()
}

// WindowSec — для admin panel чтобы показать "за последние N сек".
func (t *IPTracker) WindowSec() int { return int(t.window.Seconds()) }
