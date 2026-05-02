using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ZConectService;

/// <summary>
/// Named Pipe server that listens for connections from the ZConnect helper process.
/// Runs in the service (Session 0). Helper connects after being spawned.
/// </summary>
public sealed class PipeServer : IDisposable
{
    private readonly ServiceLogger _log;
    private readonly ServiceConfig _config;

    /// <summary>Set by ServiceWorker after unattended session is created.</summary>
    public UnattendedSessionManager? UnattendedManager { get; set; }

    /// <summary>Set by ServiceWorker — SYSTEM-context input injection (replaces UI direct injection after Stage 1).</summary>
    internal InputInjectionAgent? InputAgent { get; set; }

    /// <summary>
    /// Set by ServiceWorker — forwards mouse/keyboard to ZConectInputHelper.exe
    /// running in user session with winlogon token. Used for secure-desktop
    /// input (Winlogon) which service in session 0 cannot do itself due to
    /// window-station isolation.
    /// </summary>
    internal InputHelperManager? InputHelperManager { get; set; }

    /// <summary>Completed when unattended session codes become available.
    /// Allows hello handler to await briefly instead of sending empty config.
    /// TaskCompletionSource stays completed once set — safe for reconnecting helpers.
    /// RunContinuationsAsynchronously avoids blocking the signaling thread.</summary>
    private readonly TaskCompletionSource _sessionReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Signal that unattended session codes are available.</summary>
    public void NotifySessionReady() => _sessionReadyTcs.TrySetResult();

    /// <summary>Set when user manually exits GUI — prevents respawn.</summary>
    public bool UserExitRequested { get; private set; }

    /// <summary>UAC policy state tracking (audit fix #4 2026-04-25).
    /// True если UAC был ослаблен через pipe и UI ещё не восстановил.
    /// На pipe disconnect (UI crash / process kill / orphaned state) — auto-restore.
    /// Защищает от ситуации "malware ослабил UAC через IPC и сразу exit'нулся" —
    /// service видит disconnect и восстанавливает default policy.
    /// Также защищает от UI crash во время active session.</summary>
    private int _uacDisabledByPipe; // 0 = не трогали, 1 = disabled через pipe
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private NamedPipeServerStream? _activePipe;
    private readonly object _pipeLock = new();
    /// <summary>Serializes all pipe writes — named pipes don't support concurrent writes.</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PipeServer(ServiceLogger log, ServiceConfig config)
    {
        _log = log;
        _config = config;
    }

    /// <summary>Start listening for helper connections in background.</summary>
    public void Start(CancellationToken serviceCt)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // Allow interactive (console-logged-in) users to connect, but NOT
                // remote/service/batch accounts. Previously used AuthenticatedUserSid
                // which was too broad — any authenticated user (including network logons,
                // other services) could connect to the pipe.
                // InteractiveSid = S-1-5-4: only users with interactive logon sessions.
                var pipeSecurity = new PipeSecurity();
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));

                pipe = NamedPipeServerStreamAcl.Create(
                    PipeProtocol.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    pipeSecurity);

                _log.Debug("Pipe", "waiting_for_helper_connection");
                await pipe.WaitForConnectionAsync(ct);
                _log.Info("Pipe", "helper_connected");

                lock (_pipeLock)
                {
                    _activePipe?.Dispose();
                    _activePipe = pipe;
                }

                // SID check происходит ВНУТРИ HandleClientAsync после первого
                // ReadAsync — RunAsClient требует чтобы с pipe хоть что-то
                // считалось до установления impersonation context'а.
                await HandleClientAsync(pipe, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warn("Pipe", $"listen_error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
            finally
            {
                // Always dispose pipe before creating next one (maxInstances=1).
                lock (_pipeLock)
                {
                    if (_activePipe == pipe)
                        _activePipe = null;
                }
                try { pipe?.Dispose(); } catch (Exception ex) { _log.Debug("Pipe", $"dispose_error: {ex.Message}"); }

                // Audit fix #4 2026-04-25: если UAC был ослаблен через эту pipe
                // connection — auto-restore. UI должна была сама послать "enable"
                // при disconnect'е viewer'а; если UI крашнулась / процесс убит /
                // malware послал "disable" и exit'нулся — мы ловим disconnect
                // здесь и возвращаем policy в безопасное состояние.
                if (System.Threading.Interlocked.CompareExchange(ref _uacDisabledByPipe, 0, 1) == 1)
                {
                    try
                    {
                        SetSecureDesktopPolicy(1);
                        _log.Info("Pipe", "uac_auto_restored_on_pipe_disconnect — UI did not call enable");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("Pipe", $"uac_auto_restore_failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var sidVerified = false;
        try
        {
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                var msg = await PipeProtocol.ReadAsync(pipe, ct);
                if (msg is null)
                {
                    _log.Info("Pipe", "helper_disconnected");
                    break;
                }

                // После первого успешного ReadAsync pipe имеет impersonation
                // context готовый для RunAsClient. Раньше пытался проверять до
                // чтения → всегда IOException («данные не считаны»).
                // SID check делаем один раз за connection — перед выдачей config.
                if (!sidVerified)
                {
                    if (!VerifyClientIsActiveConsoleUser(pipe))
                    {
                        // VerifyClient* уже залогировал причину. Закрываем pipe.
                        break;
                    }
                    sidVerified = true;
                    _log.Info("Pipe", "helper_connected_sid_verified");
                }

                switch (msg.Type)
                {
                    case "hello":
                        _log.Info("Pipe", $"helper_hello pid={msg.Pid} session={msg.SessionId} shortcut={msg.LaunchedFromShortcut}");
                        // Reset in-memory user_exit flag — new GUI instance connected.
                        UserExitRequested = false;
                        // Persistent SuppressAutoSpawnUi flag: reset ТОЛЬКО если user
                        // явно запустил UI через ярлык (launched_from_shortcut=true).
                        // Иначе (UI spawned сервисом) не трогаем — сохранение
                        // user-intent: "я выключил autostart → один раз не спавни
                        // → user перезапустит через ярлык когда снова нужно".
                        if (msg.LaunchedFromShortcut && _config.SuppressAutoSpawnUi)
                        {
                            _log.Info("Pipe", "suppress_autospawn_cleared_by_user_shortcut_launch");
                            _config.SuppressAutoSpawnUi = false;
                            try { _config.Save(); }
                            catch (Exception ex) { _log.Warn("Pipe", $"config_save_failed: {ex.Message}"); }
                        }

                        // Wait briefly for unattended session to be ready (avoids
                        // sending empty config then immediately pushing real one).
                        // If session is already ready or unattended is disabled, returns instantly.
                        if (UnattendedManager is not null && !UnattendedManager.HasSession)
                        {
                            _log.Debug("Pipe", "waiting_for_session_codes");
                            // Async wait — does NOT block the pipe I/O thread.
                            // Completes when NotifySessionReady() is called, or after 5s timeout.
                            await Task.WhenAny(_sessionReadyTcs.Task, Task.Delay(5000, ct));
                        }

                        // Send config + unattended codes back (under write lock to
                        // prevent concurrent writes with PushConfigToHelperAsync).
                        await _writeLock.WaitAsync(ct);
                        try
                        {
                            var um = UnattendedManager;
                            await PipeProtocol.SendAsync(pipe, BuildConfigMessage(um), ct);
                            _log.Info("Pipe", $"config_sent login={MaskCode(um?.LoginCode)}");
                            if (string.IsNullOrEmpty(_config.TurnPassword))
                                _log.Warn("Pipe", "turn_password_not_configured_stun_only — настрой TurnPassword в service-config.json если нужен relay через NAT");
                        }
                        finally { _writeLock.Release(); }
                        break;

                    case "status":
                        _log.Info("Pipe", $"helper_status connected={msg.Connected} session={msg.CurrentSessionId}");
                        break;

                    case "user_exit":
                        _log.Info("Pipe", "user_exit_requested — will not respawn");
                        UserExitRequested = true;
                        // Persistent флаг: сохранить в config чтобы после reboot
                        // SessionMonitor тоже не спавнил UI. Сбросится когда user
                        // явно запустит UI через ярлык (hello с LaunchedFromShortcut=true).
                        if (!_config.SuppressAutoSpawnUi)
                        {
                            _config.SuppressAutoSpawnUi = true;
                            try
                            {
                                _config.Save();
                                _log.Info("Pipe", "suppress_autospawn_persisted_for_next_boot");
                            }
                            catch (Exception ex) { _log.Warn("Pipe", $"config_save_failed: {ex.Message}"); }
                        }
                        break;

                    case "set_uac_policy":
                        // UI под user token не имеет прав писать в HKLM\...\Policies.
                        // Service в SYSTEM — имеет. Регулируем PromptOnSecureDesktop
                        // чтобы UAC показывался на Default (видимый + кликабельный)
                        // вместо Winlogon (невидимый для UI capture, блокирует
                        // synthetic input).
                        //
                        // Audit fix #4 2026-04-25: track _uacDisabledByPipe state.
                        // Auto-restore при pipe disconnect (UI crash / orphan / malware
                        // try-and-exit) — см. finally block в ListenLoopAsync.
                        try
                        {
                            var disable = msg.InputAction == "disable";
                            var desired = disable ? 0 : 1;
                            SetSecureDesktopPolicy(desired);
                            _log.Info("Pipe", $"uac_secure_desktop_set_to_{desired}_via_service");
                            // Track state для auto-restore при disconnect.
                            System.Threading.Interlocked.Exchange(ref _uacDisabledByPipe, disable ? 1 : 0);
                        }
                        catch (Exception ex)
                        {
                            _log.Warn("Pipe", $"set_uac_policy_failed: {ex.GetType().Name}: {ex.Message}");
                        }
                        break;

                    case "inject_mouse":
                        // Переправляем в ZConectInputHelper (user session + winlogon token).
                        // InputHelperManager делает SendToHelperAsync через own pipe.
                        if (InputHelperManager is not null)
                            await InputHelperManager.ForwardInjectMouseAsync(
                                msg.InputAction ?? "move",
                                msg.InputX, msg.InputY,
                                msg.InputButton, msg.InputDelta, ct);
                        else _log.Warn("Pipe", "inject_mouse_received_no_helper_manager");
                        break;

                    case "inject_keyboard":
                        if (InputHelperManager is not null)
                            await InputHelperManager.ForwardInjectKeyboardAsync(
                                msg.InputAction ?? "press",
                                msg.InputVirtualKey, msg.InputScanCode, ct);
                        else _log.Warn("Pipe", "inject_keyboard_received_no_helper_manager");
                        break;

                    case "inject_launch_process":
                        _log.Info("Pipe", $"inject_launch_process_from_helper name={msg.ProcessName ?? "null"}");
                        // Защита в глубину: ProcessLauncher re-validate whitelist в SYSTEM
                        // context. Если attacker caught UI и шлёт arbitrary name — здесь
                        // отбрасывается. PipeServer запущен в session 0 SYSTEM → имеет
                        // все привилегии для CreateProcessAsUser (минует impersonation).
                        ProcessLauncher.LaunchInActiveConsoleSession(msg.ProcessName ?? string.Empty, _log);
                        break;

                    case "update_config":
                        // UI shipped новые service-owned настройки (TURN creds, STUN url).
                        // Обновим _config и персистим в service-config.json.
                        try
                        {
                            var changed = false;
                            if (msg.TurnUrl is not null && msg.TurnUrl != _config.TurnUrl)
                            { _config.TurnUrl = msg.TurnUrl; changed = true; }
                            if (msg.TurnUsername is not null && msg.TurnUsername != _config.TurnUsername)
                            { _config.TurnUsername = msg.TurnUsername; changed = true; }
                            if (msg.TurnPassword is not null && msg.TurnPassword != _config.TurnPassword)
                            { _config.TurnPassword = msg.TurnPassword; changed = true; }
                            if (msg.StunUrl is not null && msg.StunUrl != _config.StunUrl)
                            { _config.StunUrl = msg.StunUrl; changed = true; }

                            if (changed)
                            {
                                _config.Save();
                                _log.Info("Pipe", $"config_updated_from_helper turn_pass_set={!string.IsNullOrEmpty(_config.TurnPassword)}");
                            }
                            else
                            {
                                _log.Debug("Pipe", "update_config_noop — ничего не поменялось");
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Warn("Pipe", $"update_config_failed: {ex.GetType().Name}: {ex.Message}");
                        }
                        break;

                    default:
                        _log.Debug("Pipe", $"unknown_message_type: {msg.Type}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Warn("Pipe", $"client_handler_error: {ex.Message}");
        }
    }

    /// <summary>Push updated config/codes to connected helper (called when unattended session created).</summary>
    public async Task PushConfigToHelperAsync()
    {
        NamedPipeServerStream? pipe;
        lock (_pipeLock) { pipe = _activePipe; }
        if (pipe is null || !pipe.IsConnected) return;

        await _writeLock.WaitAsync();
        try
        {
            var um = UnattendedManager;
            await PipeProtocol.SendAsync(pipe, BuildConfigMessage(um));
            _log.Info("Pipe", $"config_pushed login={MaskCode(um?.LoginCode)}");
        }
        // ObjectDisposed/IO — ListenLoop закрыл pipe в параллельном потоке, ожидаемо при disconnect race.
        catch (ObjectDisposedException) { _log.Debug("Pipe", "config_push_pipe_disposed_race"); }
        catch (IOException ex) { _log.Debug("Pipe", $"config_push_io: {ex.Message}"); }
        catch (Exception ex) { _log.Warn("Pipe", $"config_push_failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Уведомить UI что active input desktop переключился (Default ↔ Winlogon).
    /// UI кэширует значение и использует для TigerVNC capture reinit. Без этого
    /// UI (под user token) не может сам определить переключение — user token
    /// не имеет GENERIC_ALL на Winlogon desktop, OpenInputDesktop фейлится.
    /// </summary>
    public async Task PushDesktopChangedAsync(string desktopName)
    {
        NamedPipeServerStream? pipe;
        lock (_pipeLock) { pipe = _activePipe; }
        if (pipe is null || !pipe.IsConnected) return;

        await _writeLock.WaitAsync();
        try
        {
            await PipeProtocol.SendAsync(pipe, new { type = "desktop_changed", desktop = desktopName });
            _log.Debug("Pipe", $"desktop_changed_pushed to={desktopName}");
        }
        catch (ObjectDisposedException) { /* helper disconnected race — silent */ }
        catch (IOException ex) { _log.Debug("Pipe", $"desktop_changed_push_io: {ex.Message}"); }
        catch (Exception ex) { _log.Warn("Pipe", $"desktop_changed_push_failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Send a message to the currently connected helper.</summary>
    public async Task SendToHelperAsync(PipeMessage message)
    {
        NamedPipeServerStream? pipe;
        lock (_pipeLock) { pipe = _activePipe; }
        if (pipe is null || !pipe.IsConnected) return;

        await _writeLock.WaitAsync();
        try
        {
            await PipeProtocol.SendAsync(pipe, message);
        }
        catch (ObjectDisposedException) { /* helper disconnected race — silent */ }
        catch (IOException ex) { _log.Debug("Pipe", $"send_to_helper_io: {ex.Message}"); }
        catch (Exception ex) { _log.Warn("Pipe", $"send_to_helper_failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Build a config PipeMessage with all fields from service config + unattended manager.</summary>
    private PipeMessage BuildConfigMessage(UnattendedSessionManager? um) => new()
    {
        Type = "config",
        SignalingUrl = _config.SignalingUrl,
        WebSocketUrl = _config.WebSocketUrl,
        StunUrl = _config.StunUrl,
        TurnUrl = _config.TurnUrl,
        TurnUsername = _config.TurnUsername,
        TurnPassword = _config.TurnPassword,
        MachineId = _config.MachineId,
        DeviceSecret = _config.DeviceSecret,
        UnattendedEnabled = _config.UnattendedEnabled,
        LoginCode = um?.LoginCode,
        PassCode = um?.PassCode,
        CurrentSessionId = um?.SessionId,
        OwnerSecret = um?.OwnerSecret,
        WsUrl = um?.WsUrl,
        WsToken = um?.WsToken,
    };

    /// <summary>Mask sensitive code for logging: "84061548" → "****1548".</summary>
    private static string MaskCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "none";
        return code.Length > 4 ? "****" + code[^4..] : "****";
    }

    /// <summary>
    /// Возвращает true если client pipe'а — это та же user identity что и active
    /// console session. Ложится в лог:
    ///   pipe_sid_verified — ok, proceed
    ///   pipe_sid_mismatch — reject, другой user попытался подключиться
    ///   pipe_sid_no_active_console — boot / logout moment, reject (будем ждать
    ///     пока появится active session)
    ///   pipe_sid_check_exception — unexpected error (impersonation failed) → REJECT
    ///   pipe_sid_check_no_identity — clientSid==null → REJECT
    ///
    /// F-01 Level 1 fix (2026-04-20): раньше на exception/null возвращали true
    /// "for compat" — это был fail-open trust boundary breach (security audit
    /// CRITICAL). Теперь fail-closed: любая неспособность верифицировать identity
    /// клиента → rejecting connection. SYSTEM service self-connections проходят
    /// через explicit LocalSystemSid check ниже, не через exception path.
    /// </summary>
    private bool VerifyClientIsActiveConsoleUser(System.IO.Pipes.NamedPipeServerStream pipe)
    {
        SecurityIdentifier? clientSid = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var clientIdentity = WindowsIdentity.GetCurrent();
                clientSid = clientIdentity.User;
            });
        }
        catch (Exception ex)
        {
            // F-01 Level 1: fail-closed. Раньше был `return true; // accepting for compat`.
            _log.Warn("Pipe", $"pipe_sid_check_exception: {ex.GetType().Name}: {ex.Message} — REJECT (fail-closed)");
            return false;
        }

        if (clientSid is null)
        {
            // F-01 Level 1: fail-closed. Раньше был `return true; // accepting for compat`.
            _log.Warn("Pipe", "pipe_sid_check_no_identity — REJECT (fail-closed)");
            return false;
        }

        // LocalSystem (S-1-5-18) допустим — это сам service, может быть диагностика.
        if (clientSid.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            _log.Debug("Pipe", "pipe_sid_localsystem_allowed");
            return true;
        }

        var activeSid = WtsActiveUserInfo.TryGetActiveConsoleUserSid();
        if (activeSid is null)
        {
            _log.Warn("Pipe", $"pipe_sid_no_active_console — reject client={MaskSid(clientSid)}");
            return false;
        }

        if (!clientSid.Equals(activeSid))
        {
            _log.Warn("Pipe", $"pipe_sid_mismatch client={MaskSid(clientSid)} active_console={MaskSid(activeSid)} — reject, другой interactive user");
            return false;
        }

        _log.Debug("Pipe", $"pipe_sid_verified {MaskSid(clientSid)}");
        return true;
    }

    /// <summary>
    /// Маскирует SID в логах — оставляет structure и последние 4 символа
    /// subAuthority, чтобы в логах не утекал полный identifier.
    /// Пример: S-1-5-21-***-***-***-1001 → S-1-5-21-*-*-*-1001
    /// </summary>
    private static string MaskSid(SecurityIdentifier sid)
    {
        var s = sid.Value;
        var parts = s.Split('-');
        if (parts.Length < 5) return s; // short SID (S-1-5-18 etc.) — не секрет
        for (var i = 4; i < parts.Length - 1; i++) parts[i] = "*";
        return string.Join('-', parts);
    }

    private const string UacPolicyRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string UacPromptOnSecureDesktopValueName = "PromptOnSecureDesktop";

    private static void SetSecureDesktopPolicy(int value)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UacPolicyRegistryPath, writable: true)
            ?? throw new InvalidOperationException("cannot_open_uac_policy_key");
        key.SetValue(UacPromptOnSecureDesktopValueName, value, Microsoft.Win32.RegistryValueKind.DWord);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listenTask?.Wait(2000); } catch { }
        lock (_pipeLock)
        {
            _activePipe?.Dispose();
            _activePipe = null;
        }
        _cts?.Dispose();
    }
}
