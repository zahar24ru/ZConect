package bruteforce

import (
	"testing"
	"time"
)

func TestIPTracker_SingleFailureRecorded(t *testing.T) {
	tr := NewIPTracker(1 * time.Minute)
	tr.RecordFailure("1.2.3.4")

	ips := tr.Snapshot(1)
	if len(ips) != 1 {
		t.Fatalf("want 1 entry, got %d", len(ips))
	}
	if ips[0].IP != "1.2.3.4" {
		t.Errorf("IP=%q, want 1.2.3.4", ips[0].IP)
	}
	if ips[0].Count != 1 {
		t.Errorf("Count=%d, want 1", ips[0].Count)
	}
}

func TestIPTracker_EmptyIPIgnored(t *testing.T) {
	tr := NewIPTracker(time.Minute)
	tr.RecordFailure("")
	ips := tr.Snapshot(1)
	if len(ips) != 0 {
		t.Errorf("empty IP не должен создать entry, got %d", len(ips))
	}
}

func TestIPTracker_CountIncrements(t *testing.T) {
	tr := NewIPTracker(time.Minute)
	for i := 0; i < 10; i++ {
		tr.RecordFailure("8.8.8.8")
	}

	ips := tr.Snapshot(1)
	if len(ips) != 1 {
		t.Fatalf("want 1 IP, got %d", len(ips))
	}
	if ips[0].Count != 10 {
		t.Errorf("Count=%d, want 10", ips[0].Count)
	}
}

// Snapshot фильтрует по minCount.
func TestIPTracker_MinCountFilter(t *testing.T) {
	tr := NewIPTracker(time.Minute)
	tr.RecordFailure("1.1.1.1") // 1
	for i := 0; i < 3; i++ {
		tr.RecordFailure("2.2.2.2") // 3
	}
	for i := 0; i < 7; i++ {
		tr.RecordFailure("3.3.3.3") // 7
	}

	// minCount=1 — все 3
	if got := len(tr.Snapshot(1)); got != 3 {
		t.Errorf("min=1: got %d, want 3", got)
	}
	// minCount=3 — 2 (3.3.3.3 + 2.2.2.2)
	if got := len(tr.Snapshot(3)); got != 2 {
		t.Errorf("min=3: got %d, want 2", got)
	}
	// minCount=5 — 1 (только 3.3.3.3)
	if got := len(tr.Snapshot(5)); got != 1 {
		t.Errorf("min=5: got %d, want 1", got)
	}
	// minCount=10 — 0
	if got := len(tr.Snapshot(10)); got != 0 {
		t.Errorf("min=10: got %d, want 0", got)
	}
}

// Snapshot sorted DESC by count.
func TestIPTracker_SnapshotSortedByCount(t *testing.T) {
	tr := NewIPTracker(time.Minute)
	tr.RecordFailure("low")             // 1
	for i := 0; i < 5; i++ { tr.RecordFailure("high") }  // 5
	for i := 0; i < 3; i++ { tr.RecordFailure("mid") }   // 3

	ips := tr.Snapshot(1)
	if ips[0].IP != "high" || ips[0].Count != 5 {
		t.Errorf("first must be 'high' (5), got %+v", ips[0])
	}
	if ips[1].IP != "mid" || ips[1].Count != 3 {
		t.Errorf("second must be 'mid' (3), got %+v", ips[1])
	}
	if ips[2].IP != "low" || ips[2].Count != 1 {
		t.Errorf("third must be 'low' (1), got %+v", ips[2])
	}
}

// Failures за пределами окна не считаются.
func TestIPTracker_WindowExpiry(t *testing.T) {
	// Очень короткое окно для теста
	tr := NewIPTracker(100 * time.Millisecond)

	// 3 failures — в окне
	for i := 0; i < 3; i++ {
		tr.RecordFailure("test-ip")
	}
	if got := tr.Snapshot(1)[0].Count; got != 3 {
		t.Errorf("immediately: Count=%d, want 3", got)
	}

	// Wait окно истекло
	time.Sleep(150 * time.Millisecond)

	// Без нового failure snapshot вернёт 0 entries (все pruned)
	ips := tr.Snapshot(1)
	if len(ips) != 0 {
		t.Errorf("после истечения окна: want 0 entries, got %d", len(ips))
	}

	// Новый failure стартует новое окно
	tr.RecordFailure("test-ip")
	ips = tr.Snapshot(1)
	if len(ips) != 1 || ips[0].Count != 1 {
		t.Errorf("после reset: want 1 entry count=1, got %+v", ips)
	}
}

// FirstSeen/LastSeen корректны.
func TestIPTracker_TimestampsCorrect(t *testing.T) {
	tr := NewIPTracker(time.Minute)

	tr.RecordFailure("ip")
	time.Sleep(50 * time.Millisecond)
	tr.RecordFailure("ip")
	time.Sleep(50 * time.Millisecond)
	tr.RecordFailure("ip")

	ips := tr.Snapshot(1)
	if len(ips) != 1 {
		t.Fatalf("want 1 entry")
	}
	s := ips[0]
	if !s.LastSeen.After(s.FirstSeen) {
		t.Errorf("LastSeen должен быть позже FirstSeen, got first=%s last=%s",
			s.FirstSeen, s.LastSeen)
	}
	gap := s.LastSeen.Sub(s.FirstSeen)
	if gap < 50*time.Millisecond || gap > 300*time.Millisecond {
		t.Errorf("gap=%s, want ~100ms", gap)
	}
}

// Concurrent RecordFailure — нет race.
func TestIPTracker_ConcurrentAccess(t *testing.T) {
	tr := NewIPTracker(time.Minute)

	const N = 200
	done := make(chan struct{}, N)
	for i := 0; i < N; i++ {
		go func(i int) {
			// Чередуем IP — нагрузка на map
			ip := "10.0.0." + string(rune('0'+(i%10)))
			tr.RecordFailure(ip)
			done <- struct{}{}
		}(i)
	}
	for i := 0; i < N; i++ {
		<-done
	}

	ips := tr.Snapshot(1)
	totalCount := 0
	for _, s := range ips {
		totalCount += s.Count
	}
	if totalCount != N {
		t.Errorf("total counts=%d, want %d (no race)", totalCount, N)
	}
}

// MaxPerIP cap — slice не должен расти бесконечно для одного IP.
func TestIPTracker_MaxPerIPCap(t *testing.T) {
	tr := NewIPTracker(time.Minute)
	tr.maxPerIP = 5 // override через field (тест-only)

	for i := 0; i < 100; i++ {
		tr.RecordFailure("spammer")
	}

	tr.mu.Lock()
	stored := len(tr.failures["spammer"])
	tr.mu.Unlock()
	if stored > 5 {
		t.Errorf("slice grew to %d, want <=5", stored)
	}
}

// WindowSec helper возвращает correct value.
func TestIPTracker_WindowSec(t *testing.T) {
	tr := NewIPTracker(300 * time.Second)
	if tr.WindowSec() != 300 {
		t.Errorf("WindowSec=%d, want 300", tr.WindowSec())
	}
}
