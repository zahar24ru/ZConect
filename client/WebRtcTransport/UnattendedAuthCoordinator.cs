using System.Text.Json;

namespace WebRtcTransport;

/// <summary>
/// Pre-offer auth handshake поверх signaling WS relay. Защищает unattended access
/// (auto-approve viewer'а без confirmation dialog'а на host'е) локальным паролем.
///
/// Message types (relay'ятся сервером без валидации content'а):
///   unattended_auth_probe    — viewer → host: «какой режим auth у тебя?»
///   unattended_auth_mode     — host → viewer: "confirmation" либо "password_or_confirmation" + salt + nonce
///   unattended_auth_password — viewer → host: proof = HMAC-SHA256(PBKDF2(pwd, salt), nonce)
///   unattended_auth_skip     — viewer → host: «пропускаю password, хочу confirmation dialog»
///   unattended_auth_result   — host → viewer: ok / wrong_password / locked_out с details
///
/// Host flow: подписываемся на WS, на probe/password/skip реагируем через ViewerProbe /
/// ViewerPasswordAttempt / ViewerSkipped events — MainViewModel подключает к
/// UnattendedAuthService + устанавливает _viewerApproved.
///
/// Viewer flow: RunViewerHandshakeAsync — send probe, await mode, опционально prompt'ит
/// user'а через passwordProvider, шлёт password/skip, ждёт result. Возвращает итог.
/// </summary>
public sealed class UnattendedAuthCoordinator : IDisposable
{
    // Не readonly — заменяется через ReplaceSignalingClient на reconnect.
    private WebSocketSignalingClient _ws;
    private readonly Action<string> _log;
    private string _sessionId = string.Empty;

    // Viewer-side pending handshake state (null когда handshake не активен)
    private TaskCompletionSource<UnattendedAuthModeMessage>? _modeTcs;
    private TaskCompletionSource<UnattendedAuthResultMessage>? _resultTcs;

    // Host-side events
    /// <summary>Host: получили probe от viewer'а. Caller должен ответить через <see cref="SendModeAsync"/>.</summary>
    public event Action? ViewerProbeReceived;

    /// <summary>Host: viewer прислал password attempt (proof + nonce context известен caller'у). Arg = base64 proof.</summary>
    public event Action<string>? ViewerPasswordReceived;

    /// <summary>Host: viewer явно skip'нул password path. Caller должен идти через confirmation dialog.</summary>
    public event Action? ViewerSkippedPassword;

    public UnattendedAuthCoordinator(WebSocketSignalingClient ws, Action<string> log)
    {
        _ws = ws ?? throw new ArgumentNullException(nameof(ws));
        _log = log ?? (_ => { });
        _ws.MessageReceived += OnMessageReceived;
    }

    public void SetSession(string sessionId) => _sessionId = sessionId;

    /// <summary>
    /// Bug fix 2026-04-27: WsReconnectLoopAsync recreates _signalingClient when WS
    /// drops (~30s idle timeout). Старый coord references disposed WS instance →
    /// probes на новом WS не доходят до handler. Solution: SignalingCoordinator
    /// уже имеет ReplaceSignalingClient — тут симметричный API для UnattendedAuth.
    /// Caller обязан вызвать после _signalingClient = new ... в reconnect path.
    /// </summary>
    public void ReplaceSignalingClient(WebSocketSignalingClient newWs)
    {
        if (newWs is null) throw new ArgumentNullException(nameof(newWs));
        _ws.MessageReceived -= OnMessageReceived;
        _ws = newWs;
        _ws.MessageReceived += OnMessageReceived;
        _log("coord_replaced_signaling_client");
    }

    public void Dispose()
    {
        _ws.MessageReceived -= OnMessageReceived;
        _modeTcs?.TrySetCanceled();
        _resultTcs?.TrySetCanceled();
    }

    // ── Host-side send helpers ─────────────────────────────────────────────

    /// <summary>Host: ответ на probe что password не настроен → viewer должен идти через confirmation.</summary>
    public Task SendModeConfirmationOnlyAsync(CancellationToken ct = default) =>
        _ws.SendAsync("unattended_auth_mode", _sessionId, new { mode = "confirmation" }, ct);

    /// <summary>Host: ответ на probe — password настроен, viewer может выбрать password path или skip.</summary>
    public Task SendModePasswordAsync(string saltBase64, string nonceBase64, CancellationToken ct = default) =>
        _ws.SendAsync("unattended_auth_mode", _sessionId, new
        {
            mode = "password_or_confirmation",
            salt = saltBase64,
            nonce = nonceBase64,
        }, ct);

    /// <summary>Host: ответ на password attempt.</summary>
    public Task SendResultAsync(
        bool ok,
        string? reason = null,
        int? attemptsLeft = null,
        int? lockoutRemainingSec = null,
        int? lockoutTier = null,
        CancellationToken ct = default) =>
        _ws.SendAsync("unattended_auth_result", _sessionId, new
        {
            ok,
            reason,
            attempts_left = attemptsLeft,
            lockout_remaining_sec = lockoutRemainingSec,
            lockout_tier = lockoutTier,
        }, ct);

    // ── Viewer-side handshake ──────────────────────────────────────────────

    /// <summary>
    /// Viewer flow: вызывается ПОСЛЕ WS connect но ДО StartAsCallerAsync (offer).
    /// Шлёт probe → ждёт mode → если password mode, вызывает passwordProvider (который
    /// показывает UI dialog) → шлёт password или skip → ждёт result.
    /// </summary>
    /// <param name="passwordProvider">
    /// Callback UI. Получает salt+nonce bytes (для HMAC compute), возвращает:
    /// string != null → password entered, coordinator shлёт password message;
    /// null → user выбрал «Wait for approval», coordinator шлёт skip.
    /// Если после wrong_password нужно retry — coordinator вызывает provider повторно
    /// (с previousAttempt info в AuthModeInfo).
    /// </param>
    /// <param name="computeProof">Callback чтобы считать HMAC (нужен Pbkdf2 + HMACSHA256 — не дублируем здесь).</param>
    /// <param name="probeTimeoutMs">Fallback когда host старой версии не отвечает на probe. Default 3000 ms.</param>
    public async Task<ViewerAuthOutcome> RunViewerHandshakeAsync(
        Func<ViewerAuthPrompt, Task<string?>> passwordProvider,
        Func<string, byte[], byte[], byte[]> computeProof,
        int probeTimeoutMs = 3000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
            throw new InvalidOperationException("SetSession must be called before RunViewerHandshakeAsync");

        // Step 1: initial probe → получаем mode + salt + nonce.
        var mode = await SendProbeAndWaitModeAsync(probeTimeoutMs, ct);
        if (mode is null)
            return ViewerAuthOutcome.FallbackToConfirmation;   // host старой версии / probe timeout

        if (mode.Mode == "confirmation" || string.IsNullOrEmpty(mode.Mode))
        {
            _log("unattended_mode_confirmation");
            return ViewerAuthOutcome.FallbackToConfirmation;
        }

        if (mode.Mode != "password_or_confirmation")
        {
            _log($"unattended_mode_unknown_{mode.Mode}");
            return ViewerAuthOutcome.FallbackToConfirmation;
        }

        var saltBytes = Convert.FromBase64String(mode.Salt ?? "");
        var nonceBytes = Convert.FromBase64String(mode.Nonce ?? "");

        // Step 2: loop — password prompt → send → wait result.
        //   retry на wrong_password, re-probe на no_challenge (host nonce expired),
        //   break на lockout / ok / skip.
        var retryInfo = default(ViewerAuthPrompt);
        while (true)
        {
            if (ct.IsCancellationRequested)
                return ViewerAuthOutcome.Cancelled;

            var password = await passwordProvider(retryInfo);
            if (password is null)
            {
                // User chose "Wait for approval" — send skip, viewer goes normal confirmation path.
                await _ws.SendAsync("unattended_auth_skip", _sessionId, new { }, ct);
                _log("unattended_skip_sent");
                return ViewerAuthOutcome.FallbackToConfirmation;
            }

            var proof = computeProof(password, saltBytes, nonceBytes);
            _resultTcs = new TaskCompletionSource<UnattendedAuthResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _ws.SendAsync("unattended_auth_password", _sessionId, new
            {
                proof = Convert.ToBase64String(proof),
            }, ct);
            _log("unattended_password_sent");

            var result = await _resultTcs.Task;
            _resultTcs = null;

            if (result.Ok)
            {
                _log("unattended_auth_ok");
                return ViewerAuthOutcome.Approved;
            }

            // wrong / locked — retry или return
            if (result.Reason == "locked_out")
            {
                _log($"unattended_locked_out remaining={result.LockoutRemainingSec}s tier={result.LockoutTier}");
                // Show lockout UI c countdown (dialog runs DispatcherTimer). Return ignored —
                // пользователь видит «разблокируется через N сек» и может закрыть окно.
                // Re-try через countdown 0 не делаем в MVP — user reconnect'ится вручную когда готов.
                var lockoutPrompt = new ViewerAuthPrompt
                {
                    IsRetry = true,
                    IsLockedOut = true,
                    LockoutRemainingSec = result.LockoutRemainingSec ?? 60,
                    LockoutTier = result.LockoutTier ?? 1,
                    LastReason = "locked_out",
                };
                try { _ = await passwordProvider(lockoutPrompt); } catch { /* ignore */ }
                return ViewerAuthOutcome.LockedOut(result.LockoutRemainingSec ?? 60, result.LockoutTier ?? 1);
            }

            // no_challenge — host nonce expired / cleared (например после lockout unlock).
            // Silent re-probe для получения fresh salt+nonce, retry с тем же password'ом
            // без показа prompt'а user'у (он уже ввёл). Если re-probe fail — abort.
            if (result.Reason == "no_challenge" || result.Reason == "malformed_proof")
            {
                _log($"unattended_stale_challenge_reprobing reason={result.Reason}");
                var freshMode = await SendProbeAndWaitModeAsync(probeTimeoutMs, ct);
                if (freshMode is null || freshMode.Mode != "password_or_confirmation")
                {
                    _log("unattended_reprobe_failed_fallback_confirmation");
                    return ViewerAuthOutcome.FallbackToConfirmation;
                }
                saltBytes = Convert.FromBase64String(freshMode.Salt ?? "");
                nonceBytes = Convert.FromBase64String(freshMode.Nonce ?? "");

                // Transparent retry с тем же password'ом (user не знает что произошла пересинхронизация).
                var retryProof = computeProof(password, saltBytes, nonceBytes);
                _resultTcs = new TaskCompletionSource<UnattendedAuthResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                await _ws.SendAsync("unattended_auth_password", _sessionId, new
                {
                    proof = Convert.ToBase64String(retryProof),
                }, ct);
                _log("unattended_password_resent_after_reprobe");

                var retryResult = await _resultTcs.Task;
                _resultTcs = null;

                if (retryResult.Ok) { _log("unattended_auth_ok_after_reprobe"); return ViewerAuthOutcome.Approved; }
                if (retryResult.Reason == "locked_out")
                    return ViewerAuthOutcome.LockedOut(retryResult.LockoutRemainingSec ?? 60, retryResult.LockoutTier ?? 1);

                // retry тоже wrong_password — нормальный retry loop с fresh attemptsLeft.
                retryInfo = new ViewerAuthPrompt
                {
                    IsRetry = true,
                    AttemptsLeft = retryResult.AttemptsLeft ?? 0,
                    LastReason = retryResult.Reason ?? "wrong_password",
                };
                _log($"unattended_wrong_password_after_reprobe attempts_left={retryInfo.AttemptsLeft}");
                continue;
            }

            // wrong_password — retry loop: setup retryInfo и повторим prompt
            retryInfo = new ViewerAuthPrompt
            {
                IsRetry = true,
                AttemptsLeft = result.AttemptsLeft ?? 0,
                LastReason = result.Reason ?? "wrong_password",
            };
            _log($"unattended_wrong_password attempts_left={retryInfo.AttemptsLeft}");
        }
    }

    /// <summary>Sends probe и ждёт mode response. Возвращает null при timeout.</summary>
    private async Task<UnattendedAuthModeMessage?> SendProbeAndWaitModeAsync(int probeTimeoutMs, CancellationToken ct)
    {
        _modeTcs = new TaskCompletionSource<UnattendedAuthModeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _ws.SendAsync("unattended_auth_probe", _sessionId, new { }, ct);
        _log("unattended_probe_sent");

        var modeTask = _modeTcs.Task;
        var completed = await Task.WhenAny(modeTask, Task.Delay(probeTimeoutMs, ct));
        if (completed != modeTask)
        {
            _log("unattended_probe_timeout_fallback_confirmation");
            _modeTcs = null;
            return null;
        }

        var m = await modeTask;
        _modeTcs = null;
        return m;
    }

    // ── WS dispatch ────────────────────────────────────────────────────────

    private void OnMessageReceived(SignalingMessage msg)
    {
        switch (msg.Type)
        {
            case "unattended_auth_probe":
                // Diagnostic 2026-04-27: log if no subscriber присвоен (значит handler chain
                // broken на host-side - InitializeUnattendedAuth не отработал).
                if (ViewerProbeReceived is null)
                    _log("coord_probe_no_subscribers");
                else
                    _log("coord_probe_dispatching");
                ViewerProbeReceived?.Invoke();
                break;

            case "unattended_auth_mode":
                var mode = new UnattendedAuthModeMessage
                {
                    Mode = TryGetString(msg.Payload, "mode"),
                    Salt = TryGetString(msg.Payload, "salt"),
                    Nonce = TryGetString(msg.Payload, "nonce"),
                };
                _modeTcs?.TrySetResult(mode);
                break;

            case "unattended_auth_password":
                var proof = TryGetString(msg.Payload, "proof");
                if (!string.IsNullOrEmpty(proof))
                    ViewerPasswordReceived?.Invoke(proof);
                break;

            case "unattended_auth_skip":
                ViewerSkippedPassword?.Invoke();
                break;

            case "unattended_auth_result":
                var result = new UnattendedAuthResultMessage
                {
                    Ok = TryGetBool(msg.Payload, "ok"),
                    Reason = TryGetString(msg.Payload, "reason"),
                    AttemptsLeft = TryGetInt(msg.Payload, "attempts_left"),
                    LockoutRemainingSec = TryGetInt(msg.Payload, "lockout_remaining_sec"),
                    LockoutTier = TryGetInt(msg.Payload, "lockout_tier"),
                };
                _resultTcs?.TrySetResult(result);
                break;
        }
    }

    private static string TryGetString(JsonElement payload, string key)
    {
        if (payload.ValueKind != JsonValueKind.Object) return string.Empty;
        if (!payload.TryGetProperty(key, out var v)) return string.Empty;
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static bool TryGetBool(JsonElement payload, string key)
    {
        if (payload.ValueKind != JsonValueKind.Object) return false;
        if (!payload.TryGetProperty(key, out var v)) return false;
        return v.ValueKind == JsonValueKind.True;
    }

    private static int? TryGetInt(JsonElement payload, string key)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (!payload.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        return null;
    }
}

// ── DTOs для viewer-flow ────────────────────────────────────────────────

public sealed class UnattendedAuthModeMessage
{
    public string Mode { get; set; } = string.Empty;
    public string? Salt { get; set; }
    public string? Nonce { get; set; }
}

public sealed class UnattendedAuthResultMessage
{
    public bool Ok { get; set; }
    public string? Reason { get; set; }
    public int? AttemptsLeft { get; set; }
    public int? LockoutRemainingSec { get; set; }
    public int? LockoutTier { get; set; }
}

public struct ViewerAuthPrompt
{
    public bool IsRetry { get; init; }
    public int AttemptsLeft { get; init; }
    public string? LastReason { get; init; }

    /// <summary>True если host только что триггернул lockout — dialog должен показать countdown,
    /// отключить input + Connect. Возврат из provider игнорируется (всегда LockedOut).</summary>
    public bool IsLockedOut { get; init; }
    /// <summary>Сколько секунд до unlock (host's view). Для countdown timer.</summary>
    public int LockoutRemainingSec { get; init; }
    /// <summary>Current tier (1/2/3/4+) — может показать «Блокировки становятся длиннее».</summary>
    public int LockoutTier { get; init; }
}

public readonly struct ViewerAuthOutcome
{
    public enum OutcomeKind { Approved, FallbackToConfirmationKind, Cancelled, LockedOutKind }

    public OutcomeKind Kind { get; }
    public int LockoutRemainingSec { get; }
    public int LockoutTier { get; }

    private ViewerAuthOutcome(OutcomeKind kind, int lockoutSec = 0, int tier = 0)
    {
        Kind = kind; LockoutRemainingSec = lockoutSec; LockoutTier = tier;
    }

    public static ViewerAuthOutcome Approved => new(OutcomeKind.Approved);
    public static ViewerAuthOutcome FallbackToConfirmation => new(OutcomeKind.FallbackToConfirmationKind);
    public static ViewerAuthOutcome Cancelled => new(OutcomeKind.Cancelled);
    public static ViewerAuthOutcome LockedOut(int sec, int tier) => new(OutcomeKind.LockedOutKind, sec, tier);

    public bool IsApproved => Kind == OutcomeKind.Approved;
    public bool IsLockedOut => Kind == OutcomeKind.LockedOutKind;
}
