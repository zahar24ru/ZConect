using System.Security.Cryptography;
using UiApp.Models;

namespace UiApp.Services;

/// <summary>
/// Host-side unattended access authentication. Каждое подключение viewer'а
/// может идти через password (тихий auto-approve) или через стандартный
/// confirmation dialog. Password хранится как PBKDF2 hash в ClientSettings,
/// verify через challenge-response: host шлёт nonce, viewer считает
/// HMAC-SHA256(PBKDF2(plaintext, salt), nonce) — сервис сверяет constant-time.
///
/// Progressive lockout на brute-force:
///   tier 1 (3 fails)  → 1  мин
///   tier 2 (6 fails)  → 5  мин
///   tier 3 (9 fails)  → 15 мин
///   tier 4+ (12+)     → 60 мин (cap)
/// Counter / tier resetятся только на successful auth или password change.
/// </summary>
public sealed class UnattendedAuthService
{
    public const int MinPasswordLength = 6;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;
    public const int DefaultPbkdf2Iterations = 100_000;

    private readonly ClientSettings _settings;
    private readonly Action _persist;
    private readonly Action<string, string> _log;
    private readonly Func<DateTime> _utcNow;
    private readonly int _pbkdf2Iterations;

    public UnattendedAuthService(
        ClientSettings settings,
        Action persist,
        Action<string, string> log,
        Func<DateTime>? utcNow = null,
        int pbkdf2Iterations = DefaultPbkdf2Iterations)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _persist = persist ?? throw new ArgumentNullException(nameof(persist));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _pbkdf2Iterations = pbkdf2Iterations > 0
            ? pbkdf2Iterations
            : DefaultPbkdf2Iterations;
    }

    /// <summary>True если host настроил пароль (и hash, и salt установлены).</summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_settings.UnattendedPasswordHash) &&
        !string.IsNullOrEmpty(_settings.UnattendedPasswordSalt);

    /// <summary>Salt для передачи viewer'у в auth_mode (Base64). Null если пароль не настроен.</summary>
    public string? SaltBase64 =>
        string.IsNullOrEmpty(_settings.UnattendedPasswordSalt) ? null : _settings.UnattendedPasswordSalt;

    /// <summary>True если прямо сейчас активен lockout.</summary>
    public bool IsLockedOut => _utcNow() < _settings.UnattendedLockoutUntilUtc;

    /// <summary>Сколько ещё осталось до unlock. Zero если не locked.</summary>
    public TimeSpan LockoutRemaining =>
        IsLockedOut ? _settings.UnattendedLockoutUntilUtc - _utcNow() : TimeSpan.Zero;

    public int CurrentLockoutTier => _settings.UnattendedLockoutTier;
    public int CurrentFailedAttempts => _settings.UnattendedFailedAttempts;

    // ── Password management (host Settings UI) ────────────────────────────

    /// <summary>
    /// Host: задать / сменить password. Throws ArgumentException если короче 6 символов.
    /// Сбрасывает lockout state полностью (логично — новый secret = новый tracking).
    /// </summary>
    public void SetPassword(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || plaintext.Length < MinPasswordLength)
            throw new ArgumentException(
                $"Password must be at least {MinPasswordLength} characters", nameof(plaintext));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Pbkdf2(plaintext, salt);

        _settings.UnattendedPasswordSalt = Convert.ToBase64String(salt);
        _settings.UnattendedPasswordHash = Convert.ToBase64String(hash);
        _settings.UnattendedFailedAttempts = 0;
        _settings.UnattendedLockoutTier = 0;
        _settings.UnattendedLockoutUntilUtc = DateTime.MinValue;
        _persist();
        _log("UnattendedAuth", "password_set");
    }

    /// <summary>Settings UI: сбросить lockout state вручную (когда tier "залип" и user хочет try again).
    /// Не трогает password hash — только counter / tier / until.</summary>
    public void ResetLockout()
    {
        _settings.UnattendedFailedAttempts = 0;
        _settings.UnattendedLockoutTier = 0;
        _settings.UnattendedLockoutUntilUtc = DateTime.MinValue;
        _persist();
        _log("UnattendedAuth", "lockout_manually_reset");
    }

    /// <summary>Host: убрать password. Caller должен также снять AllowUnattended флаг.</summary>
    public void ClearPassword()
    {
        _settings.UnattendedPasswordHash = string.Empty;
        _settings.UnattendedPasswordSalt = string.Empty;
        _settings.UnattendedFailedAttempts = 0;
        _settings.UnattendedLockoutTier = 0;
        _settings.UnattendedLockoutUntilUtc = DateTime.MinValue;
        _persist();
        _log("UnattendedAuth", "password_cleared");
    }

    // ── Challenge / verify ─────────────────────────────────────────────────

    /// <summary>
    /// Host: генерит random nonce для challenge. Не хранится здесь — caller (Connection
    /// flow) сам держит nonce между auth_mode и auth_password messages.
    /// </summary>
    public static byte[] GenerateNonce() => RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// Viewer-side helper: compute proof = HMAC-SHA256(PBKDF2(plaintext, salt), nonce).
    /// Вызывается на стороне viewer'а после того как user ввёл password.
    /// </summary>
    public byte[] ComputeProof(string plaintext, byte[] salt, byte[] nonce)
    {
        if (plaintext is null) throw new ArgumentNullException(nameof(plaintext));
        if (salt is null) throw new ArgumentNullException(nameof(salt));
        if (nonce is null) throw new ArgumentNullException(nameof(nonce));

        var derived = Pbkdf2(plaintext, salt);
        using var hmac = new HMACSHA256(derived);
        return hmac.ComputeHash(nonce);
    }

    /// <summary>
    /// Host: проверить proof от viewer'а. Обновляет lockout / attempts state и persist'ит.
    /// Результат детерминирует что ответить viewer'у в auth_result.
    /// </summary>
    public UnattendedAuthOutcome VerifyProof(byte[] proof, byte[] nonce)
    {
        if (proof is null) throw new ArgumentNullException(nameof(proof));
        if (nonce is null) throw new ArgumentNullException(nameof(nonce));

        if (!IsConfigured)
        {
            _log("UnattendedAuth", "verify_rejected_not_configured");
            return new UnattendedAuthOutcome { Result = UnattendedAuthResult.NotConfigured };
        }

        // 1) Проверка lockout ДО verify — attacker не получит signal о правильном password'е
        //    во время lockout window.
        if (IsLockedOut)
        {
            return new UnattendedAuthOutcome
            {
                Result = UnattendedAuthResult.LockedOut,
                LockoutRemaining = LockoutRemaining,
                LockoutTier = _settings.UnattendedLockoutTier,
            };
        }

        // 2) Constant-time compare.
        var storedHash = Convert.FromBase64String(_settings.UnattendedPasswordHash);
        byte[] expected;
        using (var hmac = new HMACSHA256(storedHash))
            expected = hmac.ComputeHash(nonce);

        if (ConstantTimeEquals(proof, expected))
        {
            _settings.UnattendedFailedAttempts = 0;
            _settings.UnattendedLockoutTier = 0;
            _settings.UnattendedLockoutUntilUtc = DateTime.MinValue;
            _persist();
            _log("UnattendedAuth", "auth_success");
            return new UnattendedAuthOutcome { Result = UnattendedAuthResult.Ok };
        }

        // 3) Wrong — increment + maybe escalate tier.
        _settings.UnattendedFailedAttempts++;
        var attempts = _settings.UnattendedFailedAttempts;

        if (attempts % 3 == 0)
        {
            _settings.UnattendedLockoutTier++;
            var duration = GetLockoutDuration(_settings.UnattendedLockoutTier);
            _settings.UnattendedLockoutUntilUtc = _utcNow() + duration;
            _persist();
            _log("UnattendedAuth",
                $"lockout_triggered tier={_settings.UnattendedLockoutTier} duration_min={(int)duration.TotalMinutes}");
            return new UnattendedAuthOutcome
            {
                Result = UnattendedAuthResult.LockedOut,
                LockoutRemaining = duration,
                LockoutTier = _settings.UnattendedLockoutTier,
            };
        }

        _persist();
        _log("UnattendedAuth", $"auth_failed attempts={attempts}");
        return new UnattendedAuthOutcome
        {
            Result = UnattendedAuthResult.WrongPassword,
            AttemptsLeft = 3 - (attempts % 3),
            LockoutTier = _settings.UnattendedLockoutTier,
        };
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private byte[] Pbkdf2(string plaintext, byte[] salt)
    {
        using var deriver = new Rfc2898DeriveBytes(plaintext, salt, _pbkdf2Iterations, HashAlgorithmName.SHA256);
        return deriver.GetBytes(HashBytes);
    }

    private static TimeSpan GetLockoutDuration(int tier) => tier switch
    {
        <= 0 => TimeSpan.Zero,
        1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(60),  // tier 4+ capped
    };

    private static bool ConstantTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}

public enum UnattendedAuthResult
{
    /// <summary>Verify succeeded. Host должен set _viewerApproved = true.</summary>
    Ok,
    /// <summary>Proof не совпал, attempts counter не достиг threshold → продолжение возможно.</summary>
    WrongPassword,
    /// <summary>Locked out — либо только что триггернули tier, либо lockout ещё длится.</summary>
    LockedOut,
    /// <summary>Password не настроен на host'е — viewer должен fallback на confirmation.</summary>
    NotConfigured,
}

public sealed class UnattendedAuthOutcome
{
    public UnattendedAuthResult Result { get; init; }
    /// <summary>Сколько ещё попыток до следующего lockout tier. Valid только при WrongPassword.</summary>
    public int AttemptsLeft { get; init; }
    /// <summary>Сколько ждать до unlock. Valid только при LockedOut.</summary>
    public TimeSpan LockoutRemaining { get; init; }
    /// <summary>Текущий tier (0 если никогда не triggered, 1-4+ после lockouts).</summary>
    public int LockoutTier { get; init; }
}
