using UiApp.Models;
using UiApp.Services;
using Xunit;

namespace ZConect.Tests;

/// <summary>
/// Unit tests для UnattendedAuthService — password hash/verify + progressive lockout.
/// Используется low iteration count (100) для скорости тестов; production uses 100_000.
/// </summary>
public sealed class UnattendedAuthServiceTests
{
    private const int TestIterations = 100;

    private static (UnattendedAuthService svc, ClientSettings settings, Box<DateTime> now) NewSvc()
    {
        var settings = new ClientSettings();
        var now = new Box<DateTime> { Value = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc) };
        var persistCalls = 0;
        var svc = new UnattendedAuthService(
            settings,
            persist: () => persistCalls++,
            log: (_, _) => { },
            utcNow: () => now.Value,
            pbkdf2Iterations: TestIterations);
        return (svc, settings, now);
    }

    // ── Password validation ──────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("12345")]       // 5 chars — below min
    public void SetPassword_TooShort_Throws(string pwd)
    {
        var (svc, _, _) = NewSvc();
        Assert.Throws<ArgumentException>(() => svc.SetPassword(pwd));
    }

    [Theory]
    [InlineData("123456")]      // exactly 6
    [InlineData("hunter2hunter2")]
    [InlineData("пароль с пробелом")]
    public void SetPassword_ValidLength_Succeeds(string pwd)
    {
        var (svc, settings, _) = NewSvc();
        svc.SetPassword(pwd);
        Assert.True(svc.IsConfigured);
        Assert.NotEmpty(settings.UnattendedPasswordHash);
        Assert.NotEmpty(settings.UnattendedPasswordSalt);
    }

    [Fact]
    public void SetPassword_ChangeResetsLockoutState()
    {
        var (svc, settings, _) = NewSvc();
        svc.SetPassword("initial123");
        // simulate accumulated fails
        settings.UnattendedFailedAttempts = 7;
        settings.UnattendedLockoutTier = 2;
        settings.UnattendedLockoutUntilUtc = DateTime.UtcNow.AddMinutes(5);

        svc.SetPassword("newpass456");

        Assert.Equal(0, settings.UnattendedFailedAttempts);
        Assert.Equal(0, settings.UnattendedLockoutTier);
        Assert.Equal(DateTime.MinValue, settings.UnattendedLockoutUntilUtc);
    }

    [Fact]
    public void ClearPassword_RemovesHashAndResets()
    {
        var (svc, settings, _) = NewSvc();
        svc.SetPassword("123456");
        Assert.True(svc.IsConfigured);

        svc.ClearPassword();

        Assert.False(svc.IsConfigured);
        Assert.Empty(settings.UnattendedPasswordHash);
        Assert.Empty(settings.UnattendedPasswordSalt);
    }

    // ── Verify — happy path ──────────────────────────────────────────────

    [Fact]
    public void VerifyProof_CorrectPassword_ReturnsOk()
    {
        var (svc, settings, _) = NewSvc();
        svc.SetPassword("correct-horse-battery");
        var salt = Convert.FromBase64String(settings.UnattendedPasswordSalt);
        var nonce = UnattendedAuthService.GenerateNonce();

        var proof = svc.ComputeProof("correct-horse-battery", salt, nonce);
        var outcome = svc.VerifyProof(proof, nonce);

        Assert.Equal(UnattendedAuthResult.Ok, outcome.Result);
        Assert.Equal(0, settings.UnattendedFailedAttempts);
        Assert.Equal(0, settings.UnattendedLockoutTier);
    }

    [Fact]
    public void VerifyProof_NotConfigured_ReturnsNotConfigured()
    {
        var (svc, _, _) = NewSvc();
        // пароль не задан
        var proof = new byte[32];
        var nonce = UnattendedAuthService.GenerateNonce();

        var outcome = svc.VerifyProof(proof, nonce);

        Assert.Equal(UnattendedAuthResult.NotConfigured, outcome.Result);
    }

    [Fact]
    public void VerifyProof_SuccessResetsFailedAttempts()
    {
        var (svc, settings, _) = NewSvc();
        svc.SetPassword("123456");
        var salt = Convert.FromBase64String(settings.UnattendedPasswordSalt);
        var nonce = UnattendedAuthService.GenerateNonce();

        // накидываем 2 fail'а
        svc.VerifyProof(new byte[32], nonce);
        svc.VerifyProof(new byte[32], nonce);
        Assert.Equal(2, settings.UnattendedFailedAttempts);

        // правильный proof → reset
        var proof = svc.ComputeProof("123456", salt, nonce);
        svc.VerifyProof(proof, nonce);
        Assert.Equal(0, settings.UnattendedFailedAttempts);
    }

    // ── Progressive lockout — 3/6/9/12 ───────────────────────────────────

    [Fact]
    public void VerifyProof_ThreeFails_TriggersTier1_1Minute()
    {
        var (svc, settings, now) = NewSvc();
        svc.SetPassword("123456");
        var nonce = UnattendedAuthService.GenerateNonce();
        var wrong = new byte[32]; // wrong proof

        var first = svc.VerifyProof(wrong, nonce);
        Assert.Equal(UnattendedAuthResult.WrongPassword, first.Result);
        Assert.Equal(2, first.AttemptsLeft);

        var second = svc.VerifyProof(wrong, nonce);
        Assert.Equal(UnattendedAuthResult.WrongPassword, second.Result);
        Assert.Equal(1, second.AttemptsLeft);

        var third = svc.VerifyProof(wrong, nonce);
        Assert.Equal(UnattendedAuthResult.LockedOut, third.Result);
        Assert.Equal(1, third.LockoutTier);
        Assert.Equal(TimeSpan.FromMinutes(1), third.LockoutRemaining);
        Assert.Equal(now.Value.AddMinutes(1), settings.UnattendedLockoutUntilUtc);
    }

    [Fact]
    public void VerifyProof_WithinLockout_ReturnsLockedWithoutIncrement()
    {
        var (svc, settings, now) = NewSvc();
        svc.SetPassword("123456");
        var nonce = UnattendedAuthService.GenerateNonce();
        var wrong = new byte[32];

        // trigger tier 1
        for (int i = 0; i < 3; i++) svc.VerifyProof(wrong, nonce);
        Assert.True(svc.IsLockedOut);
        var attemptsBefore = settings.UnattendedFailedAttempts;

        // ещё попытка во время lockout — counter НЕ растёт
        var outcome = svc.VerifyProof(wrong, nonce);
        Assert.Equal(UnattendedAuthResult.LockedOut, outcome.Result);
        Assert.Equal(attemptsBefore, settings.UnattendedFailedAttempts);
    }

    [Fact]
    public void VerifyProof_AfterLockoutExpires_AcceptsAttempts()
    {
        var (svc, settings, now) = NewSvc();
        svc.SetPassword("123456");
        var salt = Convert.FromBase64String(settings.UnattendedPasswordSalt);
        var nonce = UnattendedAuthService.GenerateNonce();
        var wrong = new byte[32];

        // trigger tier 1 (3 fails)
        for (int i = 0; i < 3; i++) svc.VerifyProof(wrong, nonce);
        Assert.True(svc.IsLockedOut);

        // перемотать время после lockout
        now.Value = now.Value.AddMinutes(2);
        Assert.False(svc.IsLockedOut);

        // правильный password работает
        var proof = svc.ComputeProof("123456", salt, nonce);
        var outcome = svc.VerifyProof(proof, nonce);
        Assert.Equal(UnattendedAuthResult.Ok, outcome.Result);
    }

    [Theory]
    [InlineData(3, 1, 1)]     // tier 1 = 1 min
    [InlineData(6, 2, 5)]     // tier 2 = 5 min
    [InlineData(9, 3, 15)]    // tier 3 = 15 min
    [InlineData(12, 4, 60)]   // tier 4 = 60 min
    [InlineData(15, 5, 60)]   // tier 5 = capped at 60
    [InlineData(18, 6, 60)]   // tier 6 = capped
    public void VerifyProof_ProgressiveLockoutTiers(int failCount, int expectedTier, int expectedMinutes)
    {
        var (svc, settings, now) = NewSvc();
        svc.SetPassword("123456");
        var nonce = UnattendedAuthService.GenerateNonce();
        var wrong = new byte[32];

        UnattendedAuthOutcome last = null!;
        for (int i = 0; i < failCount; i++)
        {
            // если между триггерами надо пережидать предыдущий lockout — перематываем время
            if (svc.IsLockedOut)
                now.Value = settings.UnattendedLockoutUntilUtc.AddSeconds(1);
            last = svc.VerifyProof(wrong, nonce);
        }

        Assert.Equal(UnattendedAuthResult.LockedOut, last.Result);
        Assert.Equal(expectedTier, last.LockoutTier);
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), last.LockoutRemaining);
    }

    // ── Proof correctness ────────────────────────────────────────────────

    [Fact]
    public void ComputeProof_DeterministicForSameInputs()
    {
        var (svc, _, _) = NewSvc();
        var salt = new byte[16];
        var nonce = new byte[32];
        for (int i = 0; i < 16; i++) salt[i] = (byte)i;
        for (int i = 0; i < 32; i++) nonce[i] = (byte)(i + 100);

        var proof1 = svc.ComputeProof("samepass", salt, nonce);
        var proof2 = svc.ComputeProof("samepass", salt, nonce);

        Assert.Equal(proof1, proof2);
    }

    [Fact]
    public void ComputeProof_DifferentNonceGivesDifferentProof()
    {
        var (svc, _, _) = NewSvc();
        var salt = new byte[16];
        var nonce1 = UnattendedAuthService.GenerateNonce();
        var nonce2 = UnattendedAuthService.GenerateNonce();

        var proof1 = svc.ComputeProof("samepass", salt, nonce1);
        var proof2 = svc.ComputeProof("samepass", salt, nonce2);

        Assert.NotEqual(proof1, proof2);
    }

    // ── R1 Replay defence: nonce rotation после wrong-verify ─────────────────
    // Host-side logic (clear nonce on wrong) живёт в MainViewModel, но service сам
    // должен корректно инкрементировать counter на разные proof'ы под одним nonce
    // (это сценарий когда MainViewModel не успел очистить, либо лоб-в-лоб retry
    // от реального viewer'а до того как host очистил).

    [Fact]
    public void VerifyProof_DistinctWrongProofs_EachIncrements()
    {
        var (svc, settings, _) = NewSvc();
        svc.SetPassword("correct-password");
        var nonce = UnattendedAuthService.GenerateNonce();

        // три разных wrong proof'а — разные байты в первой позиции — каждый должен
        // считаться отдельной попыткой (service pure crypto, не dedupe'ит).
        var wrong1 = new byte[32]; wrong1[0] = 1;
        var wrong2 = new byte[32]; wrong2[0] = 2;
        var wrong3 = new byte[32]; wrong3[0] = 3;

        svc.VerifyProof(wrong1, nonce);
        svc.VerifyProof(wrong2, nonce);
        var third = svc.VerifyProof(wrong3, nonce);

        Assert.Equal(UnattendedAuthResult.LockedOut, third.Result);
        Assert.Equal(3, settings.UnattendedFailedAttempts);
    }

    [Fact]
    public void VerifyProof_WrongThenCorrect_ResetsCounter()
    {
        // Регрессия: успешный proof после нескольких fail'ов сбрасывает counter
        // независимо от replay-state (важно для legit user который ввёл wrong → enter → fixed).
        var (svc, settings, _) = NewSvc();
        svc.SetPassword("correct-password");
        var salt = Convert.FromBase64String(settings.UnattendedPasswordSalt);
        var nonce = UnattendedAuthService.GenerateNonce();

        var wrong = new byte[32]; wrong[0] = 42;
        svc.VerifyProof(wrong, nonce);
        svc.VerifyProof(wrong, nonce);  // намеренно same bytes — всё равно сервис инкрементит
        Assert.Equal(2, settings.UnattendedFailedAttempts);

        var correct = svc.ComputeProof("correct-password", salt, nonce);
        var ok = svc.VerifyProof(correct, nonce);

        Assert.Equal(UnattendedAuthResult.Ok, ok.Result);
        Assert.Equal(0, settings.UnattendedFailedAttempts);
    }

    // ── Helper ────────────────────────────────────────────────────────────

    private sealed class Box<T> { public T Value { get; set; } = default!; }
}
