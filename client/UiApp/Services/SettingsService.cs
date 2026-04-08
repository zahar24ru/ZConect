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

        Directory.CreateDirectory(dir);
        SettingsPath = Path.Combine(dir, "client-settings.json");
    }

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
                settings = new ClientSettings();
            }
        }

        // Generate persistent machine ID on first run.
        if (string.IsNullOrWhiteSpace(settings.MachineId))
        {
            settings.MachineId = Guid.NewGuid().ToString("N");
            Save(settings);
        }

        return settings;
    }

    public void Save(ClientSettings settings)
    {
        var raw = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, raw);
    }
}
