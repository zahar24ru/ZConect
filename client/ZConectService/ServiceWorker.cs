namespace ZConectService;

/// <summary>
/// Main background service worker. Runs as SYSTEM in Session 0.
/// Monitors user sessions and spawns ZConnect.exe helper in the active console session.
/// </summary>
public sealed class ServiceWorker : BackgroundService
{
    private readonly ServiceLogger _log;
    private readonly ServiceConfig _config;
    private SessionMonitor? _sessionMonitor;
    private PipeServer? _pipeServer;
    private UnattendedSessionManager? _unattendedManager;
    private InputInjectionAgent? _inputAgent;
    private InputHelperManager? _inputHelperManager;

    public override void Dispose()
    {
        _inputHelperManager?.Dispose();
        _inputAgent?.Dispose();
        base.Dispose();
    }

    public ServiceWorker(ServiceLogger log, ServiceConfig config)
    {
        _log = log;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Info("Service", "service_started");
        _log.Info("Service", $"machine_id={_config.MachineId}");
        _log.Info("Service", $"helper_path={ResolveHelperPath()}");
        _log.Info("Service", $"unattended={_config.UnattendedEnabled}");
        _log.Info("Service", $"auto_spawn={_config.AutoSpawnHelper}");

        var helperPath = ResolveHelperPath();
        if (!File.Exists(helperPath))
        {
            _log.Error("Service", $"helper_exe_not_found path={helperPath}");
            _log.Error("Service", "service_stopping_no_helper");
            return;
        }

        _sessionMonitor = new SessionMonitor(_log, helperPath);
        _inputAgent = new InputInjectionAgent(_log);
        _pipeServer = new PipeServer(_log, _config) { InputAgent = _inputAgent };
        _pipeServer.Start(stoppingToken);

        // ZConectInputHelper.exe — spawn'ится в user session с winlogon token,
        // делает input injection на secure desktop (Winlogon) и уведомляет о
        // переключениях desktop'а. Путь рядом с helper'ом UI.
        var inputHelperPath = Path.Combine(Path.GetDirectoryName(helperPath)!, "ZConectInputHelper.exe");
        _inputHelperManager = new InputHelperManager(_log, inputHelperPath, _pipeServer);
        _pipeServer.InputHelperManager = _inputHelperManager;
        _inputHelperManager.Start(stoppingToken);

        // Объединённый callback: in-memory UserExitRequested (session-scoped) + persistent
        // SuppressAutoSpawnUi (пережив reboot). Оба означают "user хочет чтобы UI был off".
        // SessionMonitor consult'ит это на initial spawn, retry, и respawn after crash.
        _sessionMonitor.ShouldSuppressRespawn = () =>
            _pipeServer.UserExitRequested || _config.SuppressAutoSpawnUi;

        // Phase 3: Unattended session management (non-blocking — handles boot without network).
        if (_config.UnattendedEnabled)
        {
            _unattendedManager = new UnattendedSessionManager(_log, _config);
            _pipeServer.UnattendedManager = _unattendedManager;
            // Fire-and-forget: retry in background if network not available yet.
            _ = Task.Run(async () =>
            {
                await _unattendedManager.StartSessionAsync(stoppingToken);
                if (_unattendedManager.HasSession)
                {
                    _log.Info("Service", $"unattended_session login=****{_unattendedManager.LoginCode[^4..]} pass=****{_unattendedManager.PassCode[^4..]}");
                    // Signal pipe server that codes are ready — unblocks hello handler
                    // if it's still waiting within the 5s window.
                    _pipeServer.NotifySessionReady();
                    // Also push codes explicitly — covers two cases:
                    // 1) Hello handler already timed out (5s) and sent empty config → push delivers real codes.
                    // 2) Helper connected after session was ready → TCS already set, hello sent config, push is a harmless no-op.
                    // Write lock in PushConfigToHelperAsync prevents concurrent pipe writes.
                    await _pipeServer.PushConfigToHelperAsync();
                }
            });

        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Always spawn GUI with SYSTEM token (for UAC desktop capture).
                    // GUI gets winlogon.exe token → can call SetThreadDesktop.
                    if (_config.AutoSpawnHelper)
                    {
                        _sessionMonitor.Tick();
                    }

                    // Keep unattended session alive.
                    if (_unattendedManager is not null)
                    {
                        await _unattendedManager.TickAsync(stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("Service", "tick_exception", ex.Message);
                }

                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log.Error("Service", "fatal_exception", ex.ToString());
        }
        finally
        {
            if (_unattendedManager is not null)
                await _unattendedManager.StopSessionAsync();
            _pipeServer?.Dispose();
            _sessionMonitor?.Dispose();
            _log.Info("Service", "service_stopped");
        }
    }

    /// <summary>Find ZConnect.exe — look next to the service exe, or use config override.</summary>
    private string ResolveHelperPath()
    {
        if (!string.IsNullOrWhiteSpace(_config.HelperExePath) && File.Exists(_config.HelperExePath))
            return _config.HelperExePath;

        var dir = AppContext.BaseDirectory;

        // 1. Same directory as service exe.
        var candidate = Path.Combine(dir, "ZConnect.exe");
        if (File.Exists(candidate)) return candidate;

        // 2. Parent directory.
        var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? dir;
        candidate = Path.Combine(parent, "ZConnect.exe");
        if (File.Exists(candidate)) return candidate;

        // 3. Sibling UiApp project (dev mode: ZConectService/bin/... → UiApp/bin/...)
        // Walk up to find client/ root, then look in UiApp output.
        try
        {
            var d = new DirectoryInfo(dir);
            while (d?.Parent != null)
            {
                d = d.Parent;
                foreach (var config in new[] { "Debug", "Release" })
                {
                    var uiAppPath = Path.Combine(d.FullName, "UiApp", "bin", config, "net8.0-windows", "win-x64", "ZConnect.exe");
                    if (File.Exists(uiAppPath))
                    {
                        _log.Info("Service", $"helper_found_via_sibling path={uiAppPath}");
                        return uiAppPath;
                    }
                }
                // Stop at drive root.
                if (d.Parent == null) break;
            }
        }
        catch { /* ignore search errors */ }

        return Path.Combine(dir, "ZConnect.exe"); // will fail with helpful log
    }
}
