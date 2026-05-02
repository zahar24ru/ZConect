using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UiApp.Services;

/// <summary>
/// Polls the server's /api/v1/client/version endpoint и сравнивает с текущей
/// сборкой. Если available > current — поднимает UpdateAvailable event,
/// MainViewModel слушает и показывает badge в UI.
///
/// Не делает auto-install. Только notification; пользователь сам идёт и
/// скачивает новый installer по DownloadUrl.
///
/// Проверка на startup + раз в 6 часов. Network errors молча ignore'ятся
/// (не блокируют UI — это best-effort feature).
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _serverBaseUrl;
    private readonly Action<string>? _log;
    private readonly System.Threading.Timer _timer;

    /// <summary>Current assembly version (Major.Minor.Build; Revision игнорируется).</summary>
    private readonly Version _currentVersion;

    public event EventHandler<UpdateInfo>? UpdateAvailable;

    public UpdateChecker(HttpClient http, string serverBaseUrl, Action<string>? onLog = null)
    {
        _http = http;
        _serverBaseUrl = serverBaseUrl.TrimEnd('/');
        _log = onLog;
        var asm = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        _currentVersion = new Version(asm.Major, asm.Minor, asm.Build);
        // Timer fire-once на startup (через 5с чтобы не конкурировать с UI init),
        // потом каждые 6 часов.
        _timer = new System.Threading.Timer(_ => _ = CheckAsync(), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromHours(6));
    }

    public async Task CheckAsync()
    {
        try
        {
            var url = _serverBaseUrl + "/api/v1/client/version";
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"update_check_status={(int)resp.StatusCode}");
                return;
            }
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var info = JsonSerializer.Deserialize<UpdateInfo>(json);
            if (info is null || string.IsNullOrWhiteSpace(info.LatestVersion))
            {
                _log?.Invoke("update_check_no_info");
                return;
            }

            if (!Version.TryParse(info.LatestVersion, out var latest))
            {
                _log?.Invoke($"update_check_bad_version={info.LatestVersion}");
                return;
            }

            // Compare major.minor.build только (ignoring revision).
            var latestCmp = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            if (latestCmp > _currentVersion)
            {
                info.CurrentVersion = _currentVersion.ToString();
                _log?.Invoke($"update_available current={_currentVersion} latest={info.LatestVersion}");
                UpdateAvailable?.Invoke(this, info);
            }
            else
            {
                _log?.Invoke($"update_check_ok current={_currentVersion} latest={info.LatestVersion}");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"update_check_error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose() => _timer.Dispose();
}

public sealed class UpdateInfo
{
    [JsonPropertyName("latest_version")]
    public string LatestVersion { get; set; } = string.Empty;

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("release_notes")]
    public string ReleaseNotes { get; set; } = string.Empty;

    [JsonPropertyName("min_compatible_version")]
    public string MinCompatibleVersion { get; set; } = string.Empty;

    [JsonPropertyName("published_at")]
    public string PublishedAt { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Не из JSON — заполняется UpdateChecker'ом перед event'ом для удобства UI.</summary>
    [JsonIgnore]
    public string CurrentVersion { get; set; } = string.Empty;
}
