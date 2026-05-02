using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace UiApp.Services;

/// <summary>
/// Опрашивает сервер /api/v1/presence?logins=... чтобы узнать какие из
/// контактов (login_codes) сейчас онлайн (имеют active session).
///
/// Каждые 30 сек делает bulk query. Результат держит в concurrent dict'е.
/// UI binding смотрит PresenceChanged event + GetState(login).
///
/// "Online" = у login_code есть active non-expired session на сервере.
/// Это означает что host готов принять viewer'а. Идеально для нашего use case.
/// </summary>
public sealed class PresenceService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _serverBaseUrl;
    private readonly Action<string>? _log;
    private readonly System.Threading.Timer _timer;
    private readonly ConcurrentDictionary<string, PresenceState> _state = new();

    /// <summary>Functions returning current list of login codes to check.
    /// Устанавливается MainViewModel'ом — при изменении Address Book / Recent.</summary>
    public Func<IEnumerable<string>>? GetLogins { get; set; }

    public event EventHandler? StateChanged;

    public PresenceService(HttpClient http, string serverBaseUrl, Action<string>? onLog = null)
    {
        _http = http;
        _serverBaseUrl = serverBaseUrl.TrimEnd('/');
        _log = onLog;
        // Poll каждые 30 сек. Первый tick через 3 сек после start (не блокировать UI init).
        _timer = new System.Threading.Timer(_ => _ = PollAsync(), null,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
    }

    public PresenceState GetState(string login)
    {
        if (string.IsNullOrEmpty(login)) return PresenceState.Unknown;
        return _state.TryGetValue(login, out var s) ? s : PresenceState.Unknown;
    }

    /// <summary>Force refresh (e.g. при открытии Address Book tab).</summary>
    public Task RefreshAsync() => PollAsync();

    // Server имеет hard cap 100 logins/request. Если у user'а больше contacts —
    // chunk'им запросы чтобы все получили обновление, а не только первые 100.
    private const int PresenceChunkSize = 100;

    private async Task PollAsync()
    {
        try
        {
            var getter = GetLogins;
            if (getter == null) return;
            // Audit fix 2026-04-24 (M3): раньше .Take(100) silently dropped 101+
            // → phantom "Offline" for extra contacts, jitter в state если Dict enum
            // order unstable. Теперь chunk по 100 и merge responses.
            var logins = getter().Where(l => !string.IsNullOrEmpty(l) && l.Length == 8).Distinct().ToArray();
            if (logins.Length == 0) return;

            bool anyChanged = false;
            var seen = new HashSet<string>(logins.Length, StringComparer.OrdinalIgnoreCase);

            for (int offset = 0; offset < logins.Length; offset += PresenceChunkSize)
            {
                var chunk = logins.Skip(offset).Take(PresenceChunkSize).ToArray();
                var url = $"{_serverBaseUrl}/api/v1/presence?logins={string.Join(",", chunk)}";
                using var resp = await _http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _log?.Invoke($"presence_http_{(int)resp.StatusCode}_chunk_{offset}");
                    // Partial failure — skip этот chunk, следующий poll повторит.
                    continue;
                }
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("online", out var onlineProp))
                {
                    _log?.Invoke("presence_missing_online_field");
                    continue;
                }
                foreach (var prop in onlineProp.EnumerateObject())
                {
                    var login = prop.Name;
                    seen.Add(login);
                    var newState = prop.Value.ValueKind == JsonValueKind.True ? PresenceState.Online : PresenceState.Offline;
                    var prev = _state.TryGetValue(login, out var p) ? p : PresenceState.Unknown;
                    if (prev != newState)
                    {
                        _state[login] = newState;
                        anyChanged = true;
                    }
                }
            }
            // Remove stale entries (login больше не в запросе = удалён контакт).
            // Используем HashSet logins для O(1) contains вместо array O(n).
            var loginsSet = new HashSet<string>(logins, StringComparer.OrdinalIgnoreCase);
            foreach (var existing in _state.Keys.ToList())
            {
                if (!loginsSet.Contains(existing))
                {
                    _state.TryRemove(existing, out _);
                    anyChanged = true;
                }
            }
            if (anyChanged)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"presence_error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose() => _timer.Dispose();
}

public enum PresenceState
{
    Unknown, // сервер не ответил / нет данных
    Online,  // active session exists
    Offline, // no active session
}
