using System.IO;
using System.Text.Json;
using UiApp.Models;

namespace UiApp.Services;

/// <summary>
/// Управляет историей недавних подключений — отдельно от <see cref="AddressBookService"/>.
/// Хранится в <c>%AppData%\ZConect\recent-history.json</c>. Лимиты: top 20 записей
/// по <see cref="RecentConnection.LastConnectedUtc"/>; старше 30 дней автоматически
/// отбрасываются при <see cref="AddOrBump"/>.
///
/// Security:
///   H1 — <see cref="RecentConnection.PassCode"/> DPAPI-encrypted на диске (via
///        <see cref="DpapiHelper"/>). In-memory остаётся plaintext для UI binding.
///   M1 — <see cref="AddOrBump"/> валидирует LoginCode: 8 digits, иначе reject.
///   M4 — Load/Save/AddOrBump/Remove/Clear thread-safe через <see cref="_lock"/>.
///        Защита от race если кто-то вызовет async-parallel (в практике UI thread
///        один, но защита in-depth).
///
/// Не DPAPI-шифруем LoginCode — это short-lived public ID сессии, не секрет.
/// PassCode — secret (выдан host'ом viewer'у), шифруем CurrentUser scope.
/// </summary>
public sealed class RecentHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const int MaxEntries = 20;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>Serialize access to file I/O + normalization. Thread-safe гарантия
    /// для async-parallel callers (M4). UI thread в норме один, но защищает
    /// если кто-то добавит background migration / sync.</summary>
    private readonly object _lock = new();

    public string HistoryPath { get; }

    public RecentHistoryService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZConect");
        Directory.CreateDirectory(dir);
        HistoryPath = Path.Combine(dir, "recent-history.json");
    }

    /// <summary>Test-only constructor.</summary>
    internal RecentHistoryService(string customPath) { HistoryPath = customPath; }

    public List<RecentConnection> Load()
    {
        lock (_lock)
        {
            return LoadInternal();
        }
    }

    public void Save(List<RecentConnection> items)
    {
        lock (_lock)
        {
            SaveInternal(items);
        }
    }

    /// <summary>Вставить запись или обновить existing по совпадению LoginCode.
    /// M1: Валидирует loginCode = ровно 8 цифр, иначе no-op (invalid payload —
    /// не загрязняем историю). Применяет cap (20 записей) и age cutoff (30 дней).
    /// Thread-safe через internal lock.</summary>
    public List<RecentConnection> AddOrBump(string loginCode, string passCode)
    {
        // M1 validation: session codes server'а — 8 digits. Tighter check чем
        // просто .Length>0 — защита от bugs где caller передаёт случайную строку.
        if (string.IsNullOrEmpty(loginCode) || loginCode.Length != 8
            || !loginCode.All(char.IsDigit))
        {
            return Load();
        }
        passCode ??= string.Empty;

        lock (_lock)
        {
            var list = LoadInternal();
            var existing = list.FirstOrDefault(x => string.Equals(x.LoginCode, loginCode, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.LastConnectedUtc = DateTime.UtcNow;
                existing.PassCode = passCode;
            }
            else
            {
                list.Add(new RecentConnection
                {
                    LoginCode = loginCode,
                    PassCode = passCode,
                    LastConnectedUtc = DateTime.UtcNow,
                });
            }
            list = Normalize(list);
            SaveInternal(list);
            return list;
        }
    }

    /// <summary>Удалить запись по LoginCode. Используется когда пользователь «сохраняет»
    /// history-запись как настоящий Contact.</summary>
    public List<RecentConnection> Remove(string loginCode)
    {
        lock (_lock)
        {
            var list = LoadInternal();
            list.RemoveAll(x => string.Equals(x.LoginCode, loginCode, StringComparison.Ordinal));
            SaveInternal(list);
            return list;
        }
    }

    /// <summary>Очистить всю историю. Используется Menu → «Очистить «Недавно подключённые»».
    /// Contacts при этом не затрагиваются.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            SaveInternal(new List<RecentConnection>());
        }
    }

    // ── Internal ─────────────────────────────────────────────────────────

    private List<RecentConnection> LoadInternal()
    {
        if (!File.Exists(HistoryPath)) return new List<RecentConnection>();
        try
        {
            var raw = File.ReadAllText(HistoryPath);
            var list = JsonSerializer.Deserialize<List<RecentConnection>>(raw, JsonOptions);
            if (list is null) return new List<RecentConnection>();
            // H1: PassCode на диске DPAPI-encrypted. Расшифровываем в memory для UI.
            // Если DPAPI fail (например token corrupt / move'нут между машин) —
            // Decrypt возвращает исходную строку → graceful degrade (plaintext).
            foreach (var r in list)
            {
                if (!string.IsNullOrEmpty(r.PassCode))
                    r.PassCode = DpapiHelper.Decrypt(r.PassCode);
            }
            return list;
        }
        catch
        {
            return new List<RecentConnection>();
        }
    }

    private void SaveInternal(List<RecentConnection> items)
    {
        try
        {
            // H1: шифруем PassCode перед записью на диск. In-memory объекты остаются
            // plaintext (caller может продолжать использовать). Работаем на копиях.
            var encrypted = items.Select(r => new RecentConnection
            {
                LoginCode = r.LoginCode,
                PassCode = string.IsNullOrEmpty(r.PassCode)
                    ? string.Empty
                    : (DpapiHelper.IsEncrypted(r.PassCode) ? r.PassCode : DpapiHelper.Encrypt(r.PassCode)),
                LastConnectedUtc = r.LastConnectedUtc,
            }).ToList();
            var raw = JsonSerializer.Serialize(encrypted, JsonOptions);
            File.WriteAllText(HistoryPath, raw);
        }
        catch
        {
            // best effort — не блокируем UI из-за ошибки записи
        }
    }

    private static List<RecentConnection> Normalize(List<RecentConnection> list)
    {
        var cutoff = DateTime.UtcNow - MaxAge;
        return list
            .Where(x => x.LastConnectedUtc > cutoff)
            .OrderByDescending(x => x.LastConnectedUtc)
            .Take(MaxEntries)
            .ToList();
    }
}
