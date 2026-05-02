using System.IO;
using System.Text.Json;
using UiApp.Models;

namespace UiApp.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string SettingsPath { get; }

    public SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZConect");

        try { Directory.CreateDirectory(dir); } catch { /* ignore — Save() will fail with clear error */ }
        SettingsPath = Path.Combine(dir, "client-settings.json");
    }

    private string BackupPath => SettingsPath + ".bak";

    // F-09: fields that must be encrypted at rest via DPAPI.
    // F-03 (external audit 2026-04-18): DeviceSecret added — proof-of-device для session reuse,
    // локальная компрометация профиля = кража binding. Было plaintext до этого fix'а.
    private static readonly string[] SecretFields = {
        nameof(ClientSettings.LastPassCode),
        nameof(ClientSettings.LastLoginCode),
        nameof(ClientSettings.TurnPassword),
        nameof(ClientSettings.DeviceSecret),
        nameof(ClientSettings.UnattendedPasswordHash),
        nameof(ClientSettings.UnattendedPasswordSalt),
    };

    public ClientSettings Load()
    {
        ClientSettings settings;
        if (!File.Exists(SettingsPath))
        {
            settings = new ClientSettings();
        }
        else
        {
            try
            {
                var raw = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<ClientSettings>(raw, JsonOptions) ?? new ClientSettings();
            }
            catch
            {
                // Main file corrupted — try to recover from backup.
                settings = TryLoadBackup();
            }
        }

        // F-09: decrypt secret fields after loading.
        DecryptSecrets(settings);

        // Generate persistent machine ID on first run.
        if (string.IsNullOrWhiteSpace(settings.MachineId))
        {
            settings.MachineId = Guid.NewGuid().ToString("N");
            Save(settings);
        }

        // Legacy URL migration (2026-04-24) — одноразовый upgrade existing installs
        // с устаревшего HTTP IP endpoint на HTTPS domain. После HTTPS deployment
        // (Caddy + Let's Encrypt) сервер больше не exposes 8080 наружу → старые
        // client-settings.json с http://92.63.102.244:8080 падают при каждом
        // presence/telemetry/update poll'е. Auto-upgrade если детектим legacy URL.
        if (MigrateLegacyUrls(settings))
        {
            Save(settings);
        }

        return settings;
    }

    /// <summary>Returns true если applied migration (caller должен Save).</summary>
    private static bool MigrateLegacyUrls(ClientSettings s)
    {
        bool changed = false;
        // Known legacy defaults которые заменяются на HTTPS prod.
        if (s.ServerApiBaseUrl == "http://92.63.102.244:8080")
        {
            s.ServerApiBaseUrl = "https://connect.zconn.ru";
            changed = true;
        }
        if (s.WebSocketUrl == "ws://92.63.102.244:8080/ws")
        {
            s.WebSocketUrl = "wss://connect.zconn.ru/ws";
            changed = true;
        }
        return changed;
    }

    /// <summary>Try loading from .bak file when main file is corrupted.</summary>
    private ClientSettings TryLoadBackup()
    {
        try
        {
            if (File.Exists(BackupPath))
            {
                var raw = File.ReadAllText(BackupPath);
                var backup = JsonSerializer.Deserialize<ClientSettings>(raw, JsonOptions);
                if (backup is not null && !string.IsNullOrWhiteSpace(backup.MachineId))
                {
                    // Backup is valid — restore it as main file.
                    try { File.Copy(BackupPath, SettingsPath, overwrite: true); } catch { }
                    return backup;
                }
            }
        }
        catch { /* backup also corrupted — give up */ }
        return new ClientSettings();
    }

    public void Save(ClientSettings settings)
    {
        // F-09: encrypt secret fields before writing to disk.
        var clone = JsonSerializer.Deserialize<ClientSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;
        EncryptSecrets(clone);
        var raw = JsonSerializer.Serialize(clone, JsonOptions);

        // Create backup of current file before overwriting — protects against
        // corruption from power loss or crash mid-write.
        try
        {
            if (File.Exists(SettingsPath))
                File.Copy(SettingsPath, BackupPath, overwrite: true);
        }
        catch { /* best effort — don't block save */ }

        // Атомарная запись: write в .tmp, затем File.Move с overwrite.
        // На NTFS rename атомарен → никакой читатель не увидит пустой или
        // частично записанный файл (иначе concurrent Load во время Save мог
        // получить truncated JSON → поломка настроек).
        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, raw);
        File.Move(tempPath, SettingsPath, overwrite: true);
    }

    private static void EncryptSecrets(ClientSettings s)
    {
        if (!string.IsNullOrEmpty(s.LastPassCode) && !DpapiHelper.IsEncrypted(s.LastPassCode))
            s.LastPassCode = DpapiHelper.Encrypt(s.LastPassCode);
        if (!string.IsNullOrEmpty(s.LastLoginCode) && !DpapiHelper.IsEncrypted(s.LastLoginCode))
            s.LastLoginCode = DpapiHelper.Encrypt(s.LastLoginCode);
        if (!string.IsNullOrEmpty(s.TurnPassword) && !DpapiHelper.IsEncrypted(s.TurnPassword))
            s.TurnPassword = DpapiHelper.Encrypt(s.TurnPassword);
        // F-03 (external audit 2026-04-18): DeviceSecret — proof-of-device для session reuse;
        // локальная компрометация %AppData% = кража device binding → abuse unattended session.
        if (!string.IsNullOrEmpty(s.DeviceSecret) && !DpapiHelper.IsEncrypted(s.DeviceSecret))
            s.DeviceSecret = DpapiHelper.Encrypt(s.DeviceSecret);
        // Unattended auth password — stored as PBKDF2 hash, но salt+hash всё равно
        // защищаем DPAPI'ем (locally compromise %AppData% → offline brute force с salt становится проще).
        if (!string.IsNullOrEmpty(s.UnattendedPasswordHash) && !DpapiHelper.IsEncrypted(s.UnattendedPasswordHash))
            s.UnattendedPasswordHash = DpapiHelper.Encrypt(s.UnattendedPasswordHash);
        if (!string.IsNullOrEmpty(s.UnattendedPasswordSalt) && !DpapiHelper.IsEncrypted(s.UnattendedPasswordSalt))
            s.UnattendedPasswordSalt = DpapiHelper.Encrypt(s.UnattendedPasswordSalt);
    }

    private static void DecryptSecrets(ClientSettings s)
    {
        if (DpapiHelper.IsEncrypted(s.LastPassCode))
            s.LastPassCode = DpapiHelper.Decrypt(s.LastPassCode);
        if (DpapiHelper.IsEncrypted(s.LastLoginCode))
            s.LastLoginCode = DpapiHelper.Decrypt(s.LastLoginCode);
        if (DpapiHelper.IsEncrypted(s.TurnPassword))
            s.TurnPassword = DpapiHelper.Decrypt(s.TurnPassword);
        if (DpapiHelper.IsEncrypted(s.DeviceSecret))
            s.DeviceSecret = DpapiHelper.Decrypt(s.DeviceSecret);
        if (DpapiHelper.IsEncrypted(s.UnattendedPasswordHash))
            s.UnattendedPasswordHash = DpapiHelper.Decrypt(s.UnattendedPasswordHash);
        if (DpapiHelper.IsEncrypted(s.UnattendedPasswordSalt))
            s.UnattendedPasswordSalt = DpapiHelper.Decrypt(s.UnattendedPasswordSalt);
    }
}
