using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;

namespace UiApp.Services;

/// <summary>
/// Sends periodic heartbeat to the signaling server for telemetry.
/// Collects: machine_id, app version, OS version, UI language.
/// All errors are swallowed — telemetry must never break the app.
/// </summary>
public sealed class TelemetryService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _machineId;
    private readonly string _appVersion;
    private readonly string _osVersion;
    private readonly string _osLanguage;
    private readonly Action<string>? _onLog;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public TelemetryService(HttpClient http, string baseUrl, string machineId, Action<string>? onLog = null)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _machineId = machineId;
        _onLog = onLog;

        _appVersion = typeof(TelemetryService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _osVersion = RuntimeInformation.OSDescription;
        _osLanguage = CultureInfo.CurrentUICulture.Name;
    }

    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        // First heartbeat immediately on startup
        await SendHeartbeatAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
            catch (OperationCanceledException) { break; }

            await SendHeartbeatAsync(ct);
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                machine_id = _machineId,
                app_version = _appVersion,
                os_version = _osVersion,
                os_language = _osLanguage
            };
            await _http.PostAsJsonAsync($"{_baseUrl}/api/v1/telemetry/heartbeat", payload, ct);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _onLog?.Invoke($"telemetry_heartbeat_error_{ex.GetType().Name}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
