package session

import (
	"errors"
	"testing"
	"time"
)

// newTestService создаёт Service с коротким ttl и maxJoinAttempts=5, lockDuration
// не используется (replaced progressive tiers), но требуется конструктором.
func newTestService(t *testing.T) *Service {
	t.Helper()
	return NewService(1*time.Hour, 5, 15*time.Second)
}

func TestCreate_BasicFields(t *testing.T) {
	svc := newTestService(t)
	sess, err := svc.Create(false)
	if err != nil {
		t.Fatalf("Create: %v", err)
	}
	if sess.ID == "" {
		t.Error("ID пустой")
	}
	if len(sess.LoginCode) != 8 {
		t.Errorf("LoginCode len=%d, want 8", len(sess.LoginCode))
	}
	if len(sess.PassCode) != 8 {
		t.Errorf("PassCode len=%d, want 8", len(sess.PassCode))
	}
	if sess.State != StateCreated {
		t.Errorf("State=%s, want CREATED", sess.State)
	}
	if sess.JoinAttempts != 0 || sess.TotalFailedAttempts != 0 || sess.LockoutTier != 0 {
		t.Errorf("fresh session не должна иметь failed counters, got JoinAttempts=%d Total=%d Tier=%d",
			sess.JoinAttempts, sess.TotalFailedAttempts, sess.LockoutTier)
	}
	if sess.OwnerSecret == "" {
		t.Error("OwnerSecret должен быть сгенерирован")
	}
}

func TestJoin_Success(t *testing.T) {
	svc := newTestService(t)
	sess, _ := svc.Create(false)

	joined, err := svc.Join(sess.LoginCode, sess.PassCode)
	if err != nil {
		t.Fatalf("Join success: %v", err)
	}
	if joined.State != StatePairing {
		t.Errorf("после join State=%s, want PAIRING", joined.State)
	}
}

func TestJoin_WrongPassReturnsBadCreds(t *testing.T) {
	svc := newTestService(t)
	sess, _ := svc.Create(false)

	_, err := svc.Join(sess.LoginCode, "00000000")
	if !errors.Is(err, ErrBadCredentials) {
		t.Fatalf("want ErrBadCredentials, got %v", err)
	}
}

func TestJoin_WrongLoginReturnsBadCreds(t *testing.T) {
	svc := newTestService(t)
	svc.Create(false) // создаём чтобы был хоть один login

	_, err := svc.Join("99999999", "12345678")
	if !errors.Is(err, ErrBadCredentials) {
		t.Fatalf("want ErrBadCredentials на несущ. login, got %v", err)
	}
}

// Ключевой тест: прогрессивный lockout — после 5 wrong passcode'ов сессия
// блокируется, tier=1, LockedUntil в будущем, JoinAttempts сбрасывается.
func TestJoin_FirstTierLockAfter5Fails(t *testing.T) {
	svc := newTestService(t)
	sess, _ := svc.Create(false)

	// 5 wrong попыток
	for i := 0; i < 5; i++ {
		_, err := svc.Join(sess.LoginCode, "00000000")
		if !errors.Is(err, ErrBadCredentials) {
			t.Fatalf("attempt %d: want ErrBadCredentials, got %v", i+1, err)
		}
	}

	// 6-я попытка должна упереться в ErrLocked (не ErrBadCredentials!)
	// даже с ПРАВИЛЬНЫМ passcode'ом — lock не даёт никому зайти.
	_, err := svc.Join(sess.LoginCode, sess.PassCode)
	if !errors.Is(err, ErrLocked) {
		t.Fatalf("после 5 fails want ErrLocked, got %v", err)
	}

	// Проверяем state внутри
	svc.mu.RLock()
	internal := svc.sessionsByID[sess.ID]
	svc.mu.RUnlock()
	if internal.LockoutTier != 1 {
		t.Errorf("tier=%d, want 1", internal.LockoutTier)
	}
	if internal.JoinAttempts != 0 {
		t.Errorf("JoinAttempts=%d после tier escalate, want 0 (ресет)", internal.JoinAttempts)
	}
	if internal.TotalFailedAttempts != 5 {
		t.Errorf("TotalFailedAttempts=%d, want 5 (НЕ ресетится на lock)", internal.TotalFailedAttempts)
	}
	if internal.LockedUntil.IsZero() {
		t.Error("LockedUntil должен быть установлен")
	}
	// Первый tier — 15 секунд
	expected := 15 * time.Second
	actual := time.Until(internal.LockedUntil)
	if actual < expected-time.Second || actual > expected+time.Second {
		t.Errorf("LockedUntil ~%s, want ~%s (tier 1 = 15s)", actual, expected)
	}
}

// После истечения первого lock'а ещё 5 wrong → tier 2, более длинный lock (2 min).
func TestJoin_SecondTierLonger(t *testing.T) {
	svc := newTestService(t)
	sess, _ := svc.Create(false)

	// Tier 1: 5 fails → 15s lock
	for i := 0; i < 5; i++ {
		svc.Join(sess.LoginCode, "00000000")
	}

	// Имитируем истечение lock'а — крутим LockedUntil в прошлое
	svc.mu.Lock()
	svc.sessionsByID[sess.ID].LockedUntil = time.Now().UTC().Add(-time.Minute)
	svc.mu.Unlock()

	// Ещё 5 wrong → должен tier escalate до 2
	for i := 0; i < 5; i++ {
		svc.Join(sess.LoginCode, "00000000")
	}

	svc.mu.RLock()
	internal := svc.sessionsByID[sess.ID]
	svc.mu.RUnlock()
	if internal.LockoutTier != 2 {
		t.Errorf("tier=%d, want 2", internal.LockoutTier)
	}
	if internal.TotalFailedAttempts != 10 {
		t.Errorf("TotalFailedAttempts=%d, want 10 (cumulative)", internal.TotalFailedAttempts)
	}
	// Tier 2 = 2 минуты
	actual := time.Until(internal.LockedUntil)
	expected := 2 * time.Minute
	if actual < expected-5*time.Second || actual > expected+5*time.Second {
		t.Errorf("LockedUntil ~%s, want ~%s (tier 2 = 2min)", actual, expected)
	}
}

// После исчерпания всех tier'ов (5×4 = 20 fails) — StateBlocked, любой Join
// возвращает ErrBlocked даже с правильным passcode.
func TestJoin_BlockedAfterAllTiersExhausted(t *testing.T) {
	svc := newTestService(t)
	sess, _ := svc.Create(false)

	// Симулируем 4 полных tier'а подряд (20 wrong fails), между каждым — сбрасываем lock
	for tier := 1; tier <= 4; tier++ {
		for i := 0; i < 5; i++ {
			svc.Join(sess.LoginCode, "00000000")
		}
		// Сбрасываем lock чтобы следующий tier мог сработать
		svc.mu.Lock()
		svc.sessionsByID[sess.ID].LockedUntil = time.Now().UTC().Add(-time.Minute)
		svc.mu.Unlock()
	}

	// После 4 tier'ов — state проверим
	svc.mu.RLock()
	internal := svc.sessionsByID[sess.ID]
	svc.mu.RUnlock()

	if internal.LockoutTier != 4 {
		t.Errorf("после 4 tier cycles tier=%d, want 4", internal.LockoutTier)
	}
	// Ещё 5 fails — должно escalate в 5 → StateBlocked (превышен len(lockoutTierDurations)=4)
	for i := 0; i < 5; i++ {
		svc.Join(sess.LoginCode, "00000000")
	}

	svc.mu.RLock()
	internal = svc.sessionsByID[sess.ID]
	svc.mu.RUnlock()
	if internal.State != StateBlocked {
		t.Errorf("после 25 fails State=%s, want BLOCKED", internal.State)
	}
	if internal.LockoutTier != 5 {
		t.Errorf("tier=%d, want 5 (blocked)", internal.LockoutTier)
	}
	if internal.TotalFailedAttempts != 25 {
		t.Errorf("TotalFailedAttempts=%d, want 25", internal.TotalFailedAttempts)
	}

	// Любой Join теперь → ErrBlocked, даже с ПРАВИЛЬНЫМ passcode
	_, err := svc.Join(sess.LoginCode, sess.PassCode)
	if !errors.Is(err, ErrBlocked) {
		t.Fatalf("blocked сессия должна возвращать ErrBlocked даже с правильным pass, got %v", err)
	}
}

// Успешный Join сбрасывает ВСЕ brute-force counters (включая cumulative).
func TestJoin_SuccessResetsBruteForceCounters(t *testing.T) {
	svc := newTestService(t)
	sess, _ := svc.Create(false)

	// Накопим 3 fails (не доходя до lock threshold 5)
	for i := 0; i < 3; i++ {
		svc.Join(sess.LoginCode, "00000000")
	}

	svc.mu.RLock()
	internal := svc.sessionsByID[sess.ID]
	svc.mu.RUnlock()
	if internal.TotalFailedAttempts != 3 {
		t.Errorf("pre-success: TotalFailed=%d, want 3", internal.TotalFailedAttempts)
	}

	// Successful join
	_, err := svc.Join(sess.LoginCode, sess.PassCode)
	if err != nil {
		t.Fatalf("success join: %v", err)
	}

	svc.mu.RLock()
	internal = svc.sessionsByID[sess.ID]
	svc.mu.RUnlock()
	if internal.JoinAttempts != 0 || internal.TotalFailedAttempts != 0 || internal.LockoutTier != 0 {
		t.Errorf("после success все brute-force counters должны быть 0, got JA=%d Total=%d Tier=%d",
			internal.JoinAttempts, internal.TotalFailedAttempts, internal.LockoutTier)
	}
}

// Refresh с regeneratePass=true разблокирует BLOCKED сессию и сбрасывает все counters.
func TestRefresh_UnblocksSession(t *testing.T) {
	svc := newTestService(t)
	sess, _ := svc.Create(false)
	origPass := sess.PassCode

	// Дожимаем до StateBlocked
	for tier := 1; tier <= 5; tier++ {
		for i := 0; i < 5; i++ {
			svc.Join(sess.LoginCode, "00000000")
		}
		svc.mu.Lock()
		svc.sessionsByID[sess.ID].LockedUntil = time.Now().UTC().Add(-time.Minute)
		svc.mu.Unlock()
	}

	svc.mu.RLock()
	internal := svc.sessionsByID[sess.ID]
	svc.mu.RUnlock()
	if internal.State != StateBlocked {
		t.Fatalf("setup: expected BLOCKED, got %s", internal.State)
	}

	// Host делает password refresh
	refreshed, err := svc.Refresh(sess.ID, sess.OwnerSecret, 0, true)
	if err != nil {
		t.Fatalf("Refresh: %v", err)
	}
	if refreshed.State != StatePairing {
		t.Errorf("после refresh State=%s, want PAIRING (unblocked)", refreshed.State)
	}
	if refreshed.PassCode == origPass {
		t.Error("regeneratePass=true должен изменить PassCode")
	}
	if refreshed.TotalFailedAttempts != 0 || refreshed.LockoutTier != 0 {
		t.Errorf("refresh должен сбросить счётчики, got Total=%d Tier=%d",
			refreshed.TotalFailedAttempts, refreshed.LockoutTier)
	}

	// Новым passcode'ом должен зайти успешно
	_, err = svc.Join(sess.LoginCode, refreshed.PassCode)
	if err != nil {
		t.Errorf("join с новым pass после unblock: %v", err)
	}
}

// Refresh без regeneratePass НЕ сбрасывает brute-force state (security —
// не даём host'у accidentally разблокировать через WS reconnect refresh).
func TestRefresh_WithoutRegeneratePass_DoesNotClearCounters(t *testing.T) {
	svc := newTestService(t)
	sess, _ := svc.Create(false)

	// 3 fails (не до lock)
	for i := 0; i < 3; i++ {
		svc.Join(sess.LoginCode, "00000000")
	}

	// Refresh БЕЗ regeneratePass (WS reconnect path)
	_, err := svc.Refresh(sess.ID, sess.OwnerSecret, 0, false)
	if err != nil {
		t.Fatalf("Refresh: %v", err)
	}

	svc.mu.RLock()
	internal := svc.sessionsByID[sess.ID]
	svc.mu.RUnlock()
	// TotalFailedAttempts должен остаться 3 (регенерации пароля не было, state не меняется)
	if internal.TotalFailedAttempts != 3 {
		t.Errorf("TotalFailedAttempts=%d, want 3 (refresh без regenerate не сбрасывает)",
			internal.TotalFailedAttempts)
	}
}

// Lock не отсчитывается корректно пока still locked — Join возвращает ErrLocked
// (не ErrBadCredentials) даже с правильным pass.
func TestJoin_LockedRejectsEvenCorrectPass(t *testing.T) {
	svc := newTestService(t)
	sess, _ := svc.Create(false)

	// 5 fails → lock
	for i := 0; i < 5; i++ {
		svc.Join(sess.LoginCode, "00000000")
	}

	// Правильный pass — всё равно ErrLocked
	_, err := svc.Join(sess.LoginCode, sess.PassCode)
	if !errors.Is(err, ErrLocked) {
		t.Fatalf("locked сессия должна отвергать даже правильный pass с ErrLocked, got %v", err)
	}
}

// После истечения lock'а — правильный pass опять принимается, counters ресетятся.
func TestJoin_SuccessAfterLockExpires(t *testing.T) {
	svc := newTestService(t)
	sess, _ := svc.Create(false)

	for i := 0; i < 5; i++ {
		svc.Join(sess.LoginCode, "00000000")
	}

	// Симулируем истечение lock'а
	svc.mu.Lock()
	svc.sessionsByID[sess.ID].LockedUntil = time.Now().UTC().Add(-time.Minute)
	svc.mu.Unlock()

	// Успешный join теперь работает
	_, err := svc.Join(sess.LoginCode, sess.PassCode)
	if err != nil {
		t.Fatalf("после истечения lock'а правильный pass должен работать: %v", err)
	}
}

// Expired сессия возвращает ErrExpired и переходит в StateClosed.
func TestJoin_Expired(t *testing.T) {
	svc := NewService(50*time.Millisecond, 5, 15*time.Second)
	sess, _ := svc.Create(false)

	time.Sleep(100 * time.Millisecond)

	_, err := svc.Join(sess.LoginCode, sess.PassCode)
	if !errors.Is(err, ErrExpired) {
		t.Fatalf("want ErrExpired, got %v", err)
	}

	svc.mu.RLock()
	internal := svc.sessionsByID[sess.ID]
	svc.mu.RUnlock()
	if internal.State != StateClosed {
		t.Errorf("expired session State=%s, want CLOSED", internal.State)
	}
}

// Close требует правильный OwnerSecret.
func TestClose_RequiresOwnerSecret(t *testing.T) {
	svc := newTestService(t)
	sess, _ := svc.Create(false)

	err := svc.Close(sess.ID, "wrong-secret")
	if !errors.Is(err, ErrForbidden) {
		t.Fatalf("wrong owner secret: want ErrForbidden, got %v", err)
	}

	// После close с правильным secret — Join не работает
	err = svc.Close(sess.ID, sess.OwnerSecret)
	if err != nil {
		t.Fatalf("Close with correct secret: %v", err)
	}
	_, err = svc.Join(sess.LoginCode, sess.PassCode)
	if !errors.Is(err, ErrBadCredentials) {
		t.Errorf("closed session должна возвращать ErrBadCredentials, got %v", err)
	}
}

// MachineID reuse: второй Create с тем же MachineID + DeviceSecret → та же сессия.
func TestCreate_MachineIDReuse_WithCorrectSecret(t *testing.T) {
	svc := newTestService(t)
	sess1, _ := svc.CreateWithOpts(CreateOpts{MachineID: "machine-1", DeviceSecret: "secret-1"})

	// Retry с правильным secret — тот же session
	sess2, err := svc.CreateWithOpts(CreateOpts{MachineID: "machine-1", DeviceSecret: "secret-1"})
	if err != nil {
		t.Fatalf("reuse: %v", err)
	}
	if sess1.ID != sess2.ID {
		t.Errorf("должен вернуться тот же session, got %s vs %s", sess1.ID, sess2.ID)
	}
}

// MachineID reuse с НЕВЕРНЫМ DeviceSecret создаёт NEW session (защита от takeover).
func TestCreate_MachineIDReuse_WrongSecret_CreatesNew(t *testing.T) {
	svc := newTestService(t)
	sess1, _ := svc.CreateWithOpts(CreateOpts{MachineID: "machine-1", DeviceSecret: "secret-1"})

	sess2, _ := svc.CreateWithOpts(CreateOpts{MachineID: "machine-1", DeviceSecret: "wrong-secret"})

	if sess1.ID == sess2.ID {
		t.Error("неверный DeviceSecret НЕ должен возвращать existing session (защита от takeover)")
	}
}

// ListActive возвращает только non-closed + non-expired сессии.
func TestListActive_SkipsClosedAndExpired(t *testing.T) {
	// Короткий TTL для expired теста; третью сессию вручную bump'аем на час жизни.
	svc := NewService(50*time.Millisecond, 5, 15*time.Second)

	sess1, _ := svc.Create(false)
	sess2, _ := svc.Create(false)
	sess3, _ := svc.Create(false)

	// Bump sess3 чтобы она пережила sleep (иначе все три expire по дефолтному ttl=50ms).
	svc.mu.Lock()
	svc.sessionsByID[sess3.ID].ExpiresAt = time.Now().UTC().Add(time.Hour)
	svc.mu.Unlock()

	// sess1 — ручной close
	svc.Close(sess1.ID, sess1.OwnerSecret)

	// sess2 — пусть expire через sleep + Join triggers state update
	time.Sleep(100 * time.Millisecond)
	svc.Join(sess2.LoginCode, sess2.PassCode) // triggers expired check → state=Closed

	active := svc.ListActive()
	if len(active) != 1 {
		t.Errorf("ActiveCount=%d, want 1 (sess1 closed, sess2 expired, sess3 alive)", len(active))
	}
	if len(active) == 1 && active[0].ID != sess3.ID {
		t.Errorf("active[0].ID=%s, want %s (sess3)", active[0].ID, sess3.ID)
	}
}

// Race test — 5 concurrent Join'ов (threshold для first-tier lock) не ломают counters.
// После 5 wrong attempts сессия lock'ается, дальнейшие goroutines получат ErrLocked
// и не инкрементируют. Главная проверка — нет data race в mutex'е.
func TestJoin_ConcurrentWrongPass_NoRace(t *testing.T) {
	svc := newTestService(t)
	sess, _ := svc.Create(false)

	// Запускаем 5 goroutines (ровно threshold) — все должны попасть в wrong-pass path
	// и инкрементировать TotalFailedAttempts. Mutex гарантирует serial incrementy.
	const N = 5
	done := make(chan struct{}, N)
	for i := 0; i < N; i++ {
		go func() {
			svc.Join(sess.LoginCode, "00000000")
			done <- struct{}{}
		}()
	}
	for i := 0; i < N; i++ {
		<-done
	}

	svc.mu.RLock()
	internal := svc.sessionsByID[sess.ID]
	svc.mu.RUnlock()

	if internal.TotalFailedAttempts != N {
		t.Errorf("TotalFailedAttempts=%d, want %d (concurrent wrong-pass attempts)",
			internal.TotalFailedAttempts, N)
	}
	if internal.LockoutTier != 1 {
		t.Errorf("LockoutTier=%d, want 1 (все N hit threshold)", internal.LockoutTier)
	}
}

// Массированные concurrent attempts — проверка что lock корректно отсекает лишние
// запросы без corruption counters. 50 goroutines, сессия lock'нется на 5 hit'е,
// остальные 45 должны получить ErrLocked и НЕ инкрементировать Total.
func TestJoin_ManyConcurrentWithLock_NoCounterCorruption(t *testing.T) {
	svc := newTestService(t)
	sess, _ := svc.Create(false)

	const N = 50
	results := make(chan error, N)
	for i := 0; i < N; i++ {
		go func() {
			_, err := svc.Join(sess.LoginCode, "00000000")
			results <- err
		}()
	}
	badCreds, locked, other := 0, 0, 0
	for i := 0; i < N; i++ {
		err := <-results
		switch {
		case errors.Is(err, ErrBadCredentials):
			badCreds++
		case errors.Is(err, ErrLocked):
			locked++
		default:
			other++
		}
	}

	if other != 0 {
		t.Errorf("got %d unexpected errors (not BadCreds/Locked)", other)
	}
	// При N=50 ровно 5 должны быть BadCreds (первые, до lock), остальные 45 Locked.
	// НО порядок goroutines не guaranteed; могут быть race'ы где lock не успел
	// сработать и 6-я дошла до wrong-pass path. Проверяем только:
	// - TotalFailedAttempts соответствует числу ErrBadCredentials
	// - не больше maxJoinAttempts (5) так как после threshold lock
	svc.mu.RLock()
	internal := svc.sessionsByID[sess.ID]
	svc.mu.RUnlock()

	if internal.TotalFailedAttempts != badCreds {
		t.Errorf("TotalFailedAttempts=%d != badCreds=%d (counter corruption?)",
			internal.TotalFailedAttempts, badCreds)
	}
	if badCreds < 5 || badCreds > 5 {
		t.Errorf("badCreds=%d, want exactly 5 (threshold)", badCreds)
	}
	if locked != N-5 {
		t.Errorf("locked=%d, want %d", locked, N-5)
	}
}
