using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZConectService;

/// <summary>
/// Persistent service configuration stored in C:\ProgramData\ZConect\service-config.json.
/// Secrets encrypted with DPAPI (LocalMachine scope — accessible to any admin on this machine).
/// </summary>
public sealed class ServiceConfig
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZConect");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "service-config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ── Config fields ──────────────────────────────────────────────

    /// <summary>Unique machine identifier (generated once, survives reboots).</summary>
    public string MachineId { get; set; } = string.Empty;

    /// <summary>Signaling server URL. Default updated 2026-04-24 to HTTPS prod endpoint
    /// (was http://92.63.102.244:8080 — endpoint больше не exposes port 8080 наружу).</summary>
    public string SignalingUrl { get; set; } = "https://connect.zconn.ru";

    /// <summary>WebSocket URL for signaling. Обновлён на wss:// в 2026-04-24.</summary>
    public string WebSocketUrl { get; set; } = "wss://connect.zconn.ru/ws";

    /// <summary>STUN server URL.</summary>
    public string StunUrl { get; set; } = "stun:92.63.102.244:3478";

    /// <summary>TURN server URL.</summary>
    public string TurnUrl { get; set; } = "turn:92.63.102.244:3478";

    /// <summary>TURN username. Пустой по дефолту — с rotating TURN (RFC 7635)
    /// сервер выдаёт per-session username/credential через session response.
    /// Static "zconect" больше не работает т.к. coturn в --use-auth-secret mode.</summary>
    public string TurnUsername { get; set; } = "";

    /// <summary>
    /// TURN password (DPAPI-encrypted at rest). Пустая по умолчанию —
    /// не хардкодим секрет в бинарь (иначе любой кто декомпилит exe получает
    /// credentials к TURN relay'ю). Administrator заполняет через
    /// <see cref="C:\ProgramData\ZConect\service-config.json"/> или через UI
    /// настроек. Пустая строка → TURN не используется, соединение только через
    /// STUN + host candidates (может не пробить симметричный NAT).
    /// Existing installations не страдают: их service-config.json на диске
    /// уже содержит актуальный password и перезаписан не будет.
    /// </summary>
    public string TurnPassword { get; set; } = string.Empty;

    /// <summary>Device secret for machine_id binding (survives reboots). DPAPI-encrypted.</summary>
    public string DeviceSecret { get; set; } = string.Empty;

    /// <summary>Unattended access password (DPAPI-encrypted). Empty = disabled.</summary>
    public string UnattendedPassword { get; set; } = string.Empty;

    /// <summary>Whether unattended access is enabled.</summary>
    public bool UnattendedEnabled { get; set; } = true;

    /// <summary>Path to ZConnect.exe helper (auto-detected if empty).</summary>
    public string HelperExePath { get; set; } = string.Empty;

    /// <summary>Whether to auto-spawn helper in user session.</summary>
    public bool AutoSpawnHelper { get; set; } = true;

    /// <summary>
    /// Persistent флаг: если true, service НЕ спавнит UI на session change /
    /// boot / login. User явно закрыл UI через Меню→Выход / tray→Выход. Сбрасывается
    /// когда user снова запускает UI через ярлык (args без --from-service).
    ///
    /// Сценарий: installer → service start → spawn UI (flag=false). User→Exit →
    /// flag=true, saved. Reboot → service start → flag=true → UI не spawn'ится.
    /// User запускает ярлык → UI connects с launched_from_shortcut=true →
    /// flag=false, saved → next reboot UI снова spawns.
    /// </summary>
    public bool SuppressAutoSpawnUi { get; set; } = false;

    // ── Load / Save ────────────────────────────────────────────────

    public static ServiceConfig Load()
    {
        Directory.CreateDirectory(ConfigDir);

        ServiceConfig config;
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                config = JsonSerializer.Deserialize<ServiceConfig>(json, JsonOpts) ?? new ServiceConfig();
            }
            catch
            {
                config = new ServiceConfig();
            }
        }
        else
        {
            config = new ServiceConfig();
        }

        // Generate machine ID on first run.
        if (string.IsNullOrWhiteSpace(config.MachineId))
        {
            config.MachineId = Guid.NewGuid().ToString("N");
            config.Save();
        }

        // Legacy URL migration 2026-04-24 — one-time upgrade от legacy HTTP endpoint
        // на HTTPS domain после того как сервер перенесён за Caddy. Same pattern что
        // в UiApp's SettingsService.Load(). Сохраняется автоматически если changed.
        bool migrated = false;
        if (config.SignalingUrl == "http://92.63.102.244:8080")
        {
            config.SignalingUrl = "https://connect.zconn.ru";
            migrated = true;
        }
        if (config.WebSocketUrl == "ws://92.63.102.244:8080/ws")
        {
            config.WebSocketUrl = "wss://connect.zconn.ru/ws";
            migrated = true;
        }
        if (config.TurnUsername == "zconect")
        {
            // Static "zconect" больше не работает (rotating TURN --use-auth-secret).
            config.TurnUsername = "";
            migrated = true;
        }
        if (migrated)
        {
            config.Save();
        }

        // Decrypt secrets.
        config.TurnPassword = DpapiDecrypt(config.TurnPassword);
        config.DeviceSecret = DpapiDecrypt(config.DeviceSecret);
        config.UnattendedPassword = DpapiDecrypt(config.UnattendedPassword);

        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);

        // Clone and encrypt secrets before writing.
        var clone = JsonSerializer.Deserialize<ServiceConfig>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;
        clone.TurnPassword = DpapiEncrypt(clone.TurnPassword);
        clone.DeviceSecret = DpapiEncrypt(clone.DeviceSecret);
        clone.UnattendedPassword = DpapiEncrypt(clone.UnattendedPassword);

        var json = JsonSerializer.Serialize(clone, JsonOpts);

        // Audit fix 2026-04-24 (H3): atomic write — write to .tmp, keep .bak копию
        // previous config, then atomic File.Move. Без этого File.WriteAllText прямо
        // в ConfigPath мог оставлять truncated config при power loss mid-write →
        // следующий Load() падал на invalid JSON → fallback на new ServiceConfig()
        // → потеря MachineId/DeviceSecret/TurnPassword. Service.Save() триггерится
        // часто (user exit, shortcut launch, TURN rotation) — окно corruption шире.
        //
        // Same pattern уже в SettingsService.cs UiApp'а; дублируем здесь.
        var tmpPath = ConfigPath + ".tmp";
        var bakPath = ConfigPath + ".bak";
        File.WriteAllText(tmpPath, json);
        if (File.Exists(ConfigPath))
        {
            try { File.Copy(ConfigPath, bakPath, overwrite: true); } catch { /* ignore backup failure */ }
        }
        File.Move(tmpPath, ConfigPath, overwrite: true);
    }

    // ── DPAPI (LocalMachine scope) ─────────────────────────────────

    private static string DpapiEncrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(encrypted);
        }
        catch { return plainText; }
    }

    private static string DpapiDecrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(cipherText);
            if (bytes.Length < 16) return cipherText; // not encrypted
            var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch { return cipherText; }
    }
}
