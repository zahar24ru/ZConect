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
        if (!File.Exists(SettingsPath))
        {
            return new ClientSettings();
        }

        try
        {
            var raw = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<ClientSettings>(raw, JsonOptions) ?? new ClientSettings();
        }
        catch
        {
            return new ClientSettings();
        }
    }

    public void Save(ClientSettings settings)
    {
        var raw = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, raw);
    }
}
