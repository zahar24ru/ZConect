using System.Text;

namespace UiApp.Services;

/// <summary>
/// Monitors the active input desktop (Default vs Winlogon).
/// Detects desktop switches caused by UAC prompts, lock screen, Ctrl+Alt+Del.
/// Polls OpenInputDesktop every 500ms and fires DesktopChanged event on transitions.
///
/// NOTE: Any user can call OpenInputDesktop to READ the current desktop name.
/// To actually SWITCH a thread to Winlogon desktop (SetThreadDesktop), SYSTEM privileges are required.
/// </summary>
public sealed class DesktopMonitor : IDisposable
{
    private const uint DESKTOP_READOBJECTS = 0x0001;

    private readonly Action<string>? _onLog;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private string _currentDesktop = "Default";

    /// <summary>Fired when the active desktop changes (e.g., "Default" → "Winlogon").</summary>
    public event Action<string, string>? DesktopChanged;

    /// <summary>Current active desktop name.</summary>
    public string CurrentDesktop => _currentDesktop;

    /// <summary>True if the current desktop is Winlogon (UAC/lock/Ctrl+Alt+Del).</summary>
    public bool IsSecureDesktop => !_currentDesktop.Equals("Default", StringComparison.OrdinalIgnoreCase);

    public DesktopMonitor(Action<string>? onLog = null)
    {
        _onLog = onLog;
    }

    /// <summary>Start polling the active desktop every 500ms.</summary>
    public void Start(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollTask = Task.Run(() => PollLoop(_cts.Token));
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var name = GetInputDesktopName();
                if (name is not null && !name.Equals(_currentDesktop, StringComparison.OrdinalIgnoreCase))
                {
                    var oldDesktop = _currentDesktop;
                    _currentDesktop = name;
                    _onLog?.Invoke($"desktop_switch from={oldDesktop} to={name}");
                    DesktopChanged?.Invoke(oldDesktop, name);
                }
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"desktop_monitor_error: {ex.Message}");
            }

            try { await Task.Delay(200, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Get the name of the currently active input desktop. Returns null on failure.</summary>
    public static string? GetInputDesktopName()
    {
        // Use GENERIC_ALL — DESKTOP_READOBJECTS is denied on Winlogon desktop.
        // SYSTEM token (from service spawn) allows GENERIC_ALL on all desktops.
        var hDesktop = DesktopInterop.OpenInputDesktop(0, false, DesktopInterop.GENERIC_ALL);
        if (hDesktop == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new byte[256];
            if (DesktopInterop.GetUserObjectInformation(hDesktop, DesktopInterop.UOI_NAME, buffer, buffer.Length, out var needed))
            {
                // UOI_NAME returns null-terminated Unicode string.
                var name = Encoding.Unicode.GetString(buffer, 0, needed).TrimEnd('\0');
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            return null;
        }
        finally
        {
            DesktopInterop.CloseDesktop(hDesktop);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _pollTask?.Wait(1000); } catch { }
        _cts?.Dispose();
    }
}
