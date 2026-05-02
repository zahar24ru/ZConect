using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace ZConect.Tests;

/// <summary>
/// Пост-сессионные проверки по JSONL-логам ZConect.
/// Запуск: <c>dotnet test --filter Category=LogAudit</c>.
///
/// Рабочий процесс:
///   1. Поставь свежий installer на host и viewer.
///   2. Проведи типовой сценарий: подключись, подвигай мышкой, смени буфер,
///      загрузи/скачай файл, отключись.
///   3. Скопируй <c>C:\ProgramData\ZConect\logs\ui.log</c> c обеих машин в репу:
///        <c>bug/viewer.ui.log</c>  — с машины-viewer'а
///        <c>bug/host.ui.log</c>    — с машины-host'а
///      (host-лог не обязателен для главного регресс-гарда, но нужен для части тестов).
///   4. <c>dotnet test --filter Category=LogAudit</c>.
///
/// Если логи не предоставлены — соответствующие тесты «проходят» с пометкой SKIPPED
/// в Output (в релиз-пайплайне просто не запускаются).
/// </summary>
[Trait("Category", "LogAudit")]
public sealed class LogAuditTests
{
    private readonly ITestOutputHelper _out;
    private static readonly string BugDir = ResolveBugDir();

    public LogAuditTests(ITestOutputHelper output) => _out = output;

    // ── Инфраструктура ───────────────────────────────────────────────────

    private static string ResolveBugDir()
    {
        // Подняться от bin/ до корня репозитория, где лежит bug/.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "bug");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "bug");
    }

    public sealed record LogEvent(DateTime Ts, string Level, string Module, string EventName, string? Error);

    private List<LogEvent> ParseLog(string path)
    {
        var events = new List<LogEvent>();
        var lineNo = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var ts = root.GetProperty("ts").GetDateTime().ToUniversalTime();
                var level = root.GetProperty("level").GetString() ?? "";
                var module = root.GetProperty("module").GetString() ?? "";
                var eventName = root.GetProperty("event_name").GetString() ?? "";
                string? error = null;
                if (root.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String)
                    error = errProp.GetString();
                events.Add(new LogEvent(ts, level, module, eventName, error));
            }
            catch (JsonException)
            {
                _out.WriteLine($"skipped malformed line {lineNo} in {Path.GetFileName(path)}");
            }
        }
        return events;
    }

    /// <summary>Возвращает null если файл отсутствует — тест мягко пропускается.</summary>
    private List<LogEvent>? LoadLog(string name)
    {
        var path = Path.Combine(BugDir, name);
        if (!File.Exists(path))
        {
            _out.WriteLine($"SKIPPED: {path} отсутствует. Скопируй ui.log в bug/ как {name}.");
            return null;
        }
        var events = ParseLog(path);
        _out.WriteLine($"Загружено {events.Count} событий из {name}");
        return events;
    }

    /// <summary>
    /// Оставляет события только из ПОСЛЕДНЕЙ сессии (от последнего ws_connected и далее).
    /// Файл ротируется при 5MB, поэтому может содержать несколько сессий подряд.
    /// </summary>
    private static List<LogEvent> LatestSession(List<LogEvent> events)
    {
        var lastWsConnected = events.LastOrDefault(e => e.EventName == "ws_connected");
        if (lastWsConnected is null) return events;
        return events.Where(e => e.Ts >= lastWsConnected.Ts).ToList();
    }

    private static LogEvent? FirstMatch(IEnumerable<LogEvent> events, string prefix) =>
        events.FirstOrDefault(e => e.EventName.StartsWith(prefix, StringComparison.Ordinal));

    private static List<LogEvent> AllMatches(IEnumerable<LogEvent> events, string prefix) =>
        events.Where(e => e.EventName.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    private static string Ago(DateTime ts, DateTime from) =>
        $"{(ts - from).TotalMilliseconds:+0;-0;0}ms";

    // ════════════════════════════════════════════════════════════
    //  VIEWER — регресс-гарды
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Главный регресс-гард: screen_meta должен прийти на viewer
    /// не позже 3 секунд после открытия Control DataChannel.
    /// Если &gt; 3s — setup-loop на хосте снова скипает screen_meta (баг 2026-04-17).
    /// </summary>
    [Fact]
    public void Viewer_screen_meta_arrives_within_3s_of_DC_open()
    {
        var raw = LoadLog("viewer.ui.log");
        if (raw is null) return;
        var events = LatestSession(raw);

        var dcOpened = FirstMatch(events, "channel_opened_event_at_attempt");
        Assert.True(dcOpened is not null, "channel_opened_event_at_attempt не найден — viewer не подключился.");

        var screenMeta = FirstMatch(events, "screen_meta_received");
        Assert.True(screenMeta is not null, "screen_meta_received не найден — host не прислал screen_meta за всю сессию.");

        var delayMs = (screenMeta!.Ts - dcOpened!.Ts).TotalMilliseconds;
        _out.WriteLine($"DC opened:    {dcOpened.Ts:HH:mm:ss.fff}");
        _out.WriteLine($"screen_meta:  {screenMeta.Ts:HH:mm:ss.fff}  ({screenMeta.EventName})");
        _out.WriteLine($"delay:        {delayMs:F0}ms (лимит 3000ms)");

        Assert.True(delayMs <= 3000, $"screen_meta задержался на {delayMs:F0}ms после DC open (>3000ms = регресс setup-loop'а).");
    }

    /// <summary>
    /// Виден ли лог mouse_input_blocked_no_screen_meta — значит screen_meta
    /// вообще не дошёл в момент когда пользователь двигал мышку.
    /// </summary>
    [Fact]
    public void Viewer_no_mouse_input_blocked_warnings()
    {
        var raw = LoadLog("viewer.ui.log");
        if (raw is null) return;
        var events = LatestSession(raw);

        var blocked = AllMatches(events, "mouse_input_blocked_no_screen_meta");
        if (blocked.Count > 0)
        {
            _out.WriteLine($"Найдено {blocked.Count} блокировок mouse_input_blocked_no_screen_meta:");
            foreach (var e in blocked.Take(5)) _out.WriteLine($"  {e.Ts:HH:mm:ss.fff}");
        }
        Assert.Empty(blocked);
    }

    /// <summary>
    /// screen_meta должен прийти ДО или вплотную с первым кадром, иначе
    /// есть окно в котором viewer рендерит, а mouse-input скипается.
    /// </summary>
    [Fact]
    public void Viewer_screen_meta_arrives_before_or_near_first_frame()
    {
        var raw = LoadLog("viewer.ui.log");
        if (raw is null) return;
        var events = LatestSession(raw);

        var screenMeta = FirstMatch(events, "screen_meta_received");
        var firstFrame = FirstMatch(events, "first_remote_video_frame_received");
        Assert.True(screenMeta is not null, "screen_meta_received не найден.");
        Assert.True(firstFrame is not null, "first_remote_video_frame_received не найден.");

        var delayMs = (screenMeta!.Ts - firstFrame!.Ts).TotalMilliseconds;
        _out.WriteLine($"screen_meta vs first_frame: {delayMs:+0;-0}ms (отрицательное = screen_meta раньше, хорошо)");
        Assert.True(delayMs <= 500, $"screen_meta пришёл на {delayMs:F0}ms ПОСЛЕ первого кадра (>500ms = проблема).");
    }

    /// <summary>
    /// Первый кадр должен прийти в пределах 5 секунд от открытия DC.
    /// Если дольше — проблема с видео-пайплайном на хосте (DXGI init, encoder).
    /// </summary>
    [Fact]
    public void Viewer_first_frame_arrives_within_5s_of_DC_open()
    {
        var raw = LoadLog("viewer.ui.log");
        if (raw is null) return;
        var events = LatestSession(raw);

        var dcOpened = FirstMatch(events, "channel_opened_event_at_attempt");
        var firstFrame = FirstMatch(events, "first_remote_video_frame_received");
        Assert.True(dcOpened is not null && firstFrame is not null, "нужны channel_opened и first_remote_video_frame.");

        var delayMs = (firstFrame!.Ts - dcOpened!.Ts).TotalMilliseconds;
        _out.WriteLine($"first_frame delay:  {delayMs:F0}ms");
        Assert.True(delayMs <= 5000, $"первый кадр через {delayMs:F0}ms после DC open (>5000ms).");
    }

    // ════════════════════════════════════════════════════════════
    //  HOST — проверки со стороны отправителя
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Host должен отправить screen_meta в пределах 2 секунд после
    /// открытия Control DataChannel (через Fix A в ConfigureLocalVideoAsync
    /// или через setup-loop retry).
    /// </summary>
    [Fact]
    public void Host_sends_screen_meta_within_2s_of_DC_open()
    {
        var raw = LoadLog("host.ui.log");
        if (raw is null) return;
        var events = LatestSession(raw);

        var dcOpened = FirstMatch(events, "channel_opened_event_at_attempt");
        Assert.True(dcOpened is not null, "на host-е DC не открылся.");

        var screenMetaSent = events.FirstOrDefault(e =>
            e.EventName.Contains("dc_sent", StringComparison.Ordinal) &&
            e.EventName.Contains("screen_meta", StringComparison.Ordinal));
        Assert.True(screenMetaSent is not null, "host не отправил screen_meta — setup-loop или Fix A не сработали.");

        var delayMs = (screenMetaSent!.Ts - dcOpened!.Ts).TotalMilliseconds;
        _out.WriteLine($"host screen_meta send delay: {delayMs:F0}ms (лимит 2000ms)");
        Assert.True(delayMs <= 2000, $"host отправил screen_meta на {delayMs:F0}ms (>2000ms).");
    }

    /// <summary>
    /// Информационный: если Fix A падал потому что DC ещё не был открыт,
    /// это не ошибка сама по себе (setup-loop дослылает), но полезно видеть.
    /// </summary>
    [Fact]
    public void Host_screen_meta_send_deferred_is_informational_only()
    {
        var raw = LoadLog("host.ui.log");
        if (raw is null) return;
        var events = LatestSession(raw);

        var deferred = AllMatches(events, "screen_meta_send_deferred");
        if (deferred.Count == 0) { _out.WriteLine("screen_meta_send_deferred не было — ок."); return; }

        _out.WriteLine($"INFO: {deferred.Count} раз сработал Fix A до открытия DC (setup-loop должен был дослать):");
        foreach (var e in deferred.Take(3)) _out.WriteLine($"  {e.Ts:HH:mm:ss.fff}: {e.Error ?? e.EventName}");
        // Не ассертим — информационное событие.
    }

    // ════════════════════════════════════════════════════════════
    //  FILE TRANSFER — если пользователь передавал файлы
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Все начавшиеся передачи файлов должны закончиться file_transfer_complete
    /// (или file_ack_received с success=true). Отсутствие завершения = зависший transfer.
    /// </summary>
    [Fact]
    public void FT_every_start_has_matching_completion()
    {
        var viewer = LoadLog("viewer.ui.log");
        var host = LoadLog("host.ui.log");
        if (viewer is null && host is null) return;

        var combined = new List<LogEvent>();
        if (viewer is not null) combined.AddRange(LatestSession(viewer));
        if (host is not null) combined.AddRange(LatestSession(host));

        var starts = combined.Count(e => e.EventName.StartsWith("file_transfer_start", StringComparison.Ordinal)
                                         || e.EventName.StartsWith("ft_send_start", StringComparison.Ordinal));
        var completes = combined.Count(e => e.EventName.Contains("file_transfer_complete", StringComparison.Ordinal)
                                            || e.EventName.Contains("ft_send_complete", StringComparison.Ordinal)
                                            || e.EventName.Contains("file_ack", StringComparison.Ordinal));

        if (starts == 0)
        {
            _out.WriteLine("SKIPPED: в логах нет событий передачи файлов.");
            return;
        }

        _out.WriteLine($"FT: {starts} начато, {completes} завершено");
        Assert.True(completes >= starts, $"{starts - completes} передач файлов не завершились.");
    }

    // ════════════════════════════════════════════════════════════
    //  ERROR / WARN — незапланированные ошибки
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Неожиданные ERROR события. Whitelist — известные/допустимые ошибки
    /// (например, игнорируемые disconnect'ы при завершении).
    /// </summary>
    [Fact]
    public void No_unexpected_error_level_events()
    {
        var viewer = LoadLog("viewer.ui.log");
        var host = LoadLog("host.ui.log");
        if (viewer is null && host is null) return;

        // Префиксы разрешённых ошибок (расширять по мере появления известных rough edges).
        var whitelist = new[]
        {
            "ws_connect_failed",         // допустимо при потере сети в процессе disconnect
            "viewer_settings_request_send_failed", // если DC закрылся в момент смены качества
        };

        var allErrors = new List<(string source, LogEvent ev)>();
        if (viewer is not null)
            allErrors.AddRange(LatestSession(viewer).Where(e => e.Level == "ERROR").Select(e => ("viewer", e)));
        if (host is not null)
            allErrors.AddRange(LatestSession(host).Where(e => e.Level == "ERROR").Select(e => ("host", e)));

        var unexpected = allErrors
            .Where(x => !whitelist.Any(w => x.ev.EventName.StartsWith(w, StringComparison.Ordinal)))
            .ToList();

        if (unexpected.Count > 0)
        {
            _out.WriteLine($"Найдено {unexpected.Count} неожиданных ERROR:");
            foreach (var (src, ev) in unexpected.Take(10))
                _out.WriteLine($"  [{src}] {ev.Ts:HH:mm:ss.fff} {ev.Module}: {ev.EventName} — {ev.Error}");
        }
        Assert.Empty(unexpected);
    }

    // ════════════════════════════════════════════════════════════
    //  DIAGNOSTICS — печать таймлайна (никогда не падает)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Печатает таймлайн ключевых событий последней сессии.
    /// Этот «тест» всегда проходит — нужен чтобы посмотреть что и когда случилось.
    /// </summary>
    [Fact]
    public void Print_viewer_session_timeline()
    {
        var raw = LoadLog("viewer.ui.log");
        if (raw is null) return;
        var events = LatestSession(raw);

        var markers = new[]
        {
            "ws_connected", "PEER_CONNECTED", "ICE_STATE_CHANGED: Connected",
            "channel_opened_event_at_attempt", "first_remote_video_frame_received",
            "screen_meta_received", "mouse_input_blocked_no_screen_meta",
            "viewer_settings_request_sent", "file_transfer_start", "file_transfer_complete",
            "viewer_disconnect_requested"
        };

        DateTime? t0 = null;
        var printed = 0;
        foreach (var ev in events)
        {
            if (!markers.Any(m => ev.EventName.StartsWith(m, StringComparison.Ordinal))) continue;
            t0 ??= ev.Ts;
            var dt = (ev.Ts - t0.Value).TotalMilliseconds;
            _out.WriteLine($"[+{dt,7:F0}ms] {ev.Level,-5} {ev.Module,-14} {ev.EventName}");
            printed++;
        }
        _out.WriteLine($"-- {printed} маркеров в последней сессии --");
    }

    [Fact]
    public void Print_host_session_timeline()
    {
        var raw = LoadLog("host.ui.log");
        if (raw is null) return;
        var events = LatestSession(raw);

        var markers = new[]
        {
            "ws_connected", "PEER_CONNECTED", "ICE_STATE_CHANGED: Connected",
            "channel_opened_event_at_attempt", "local_video_configured",
            "local_video_started_on_offer", "screen_meta_send_deferred",
            "dc_sent:Control:screen_meta", "dc_sent:Control:host_displays",
            "viewer_confirm_approved", "host_settings_applied",
        };

        DateTime? t0 = null;
        var printed = 0;
        foreach (var ev in events)
        {
            if (!markers.Any(m => ev.EventName.StartsWith(m, StringComparison.Ordinal))) continue;
            t0 ??= ev.Ts;
            var dt = (ev.Ts - t0.Value).TotalMilliseconds;
            _out.WriteLine($"[+{dt,7:F0}ms] {ev.Level,-5} {ev.Module,-14} {ev.EventName}");
            printed++;
        }
        _out.WriteLine($"-- {printed} маркеров в последней сессии host'а --");
    }
}
