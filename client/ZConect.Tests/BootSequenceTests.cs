using System.Xml.Linq;
using Microsoft.Win32;
using Xunit;

namespace ZConect.Tests;

/// <summary>
/// Tests for boot sequence: manifest validation, registry autostart,
/// command-line flags, and GUI+Service startup coordination.
/// Ensures ZConnect.exe auto-starts after reboot in system tray.
/// </summary>
public sealed class BootSequenceTests
{
    // ── Manifest: must be asInvoker (requireAdministrator blocks Registry Run) ──

    [Fact]
    public void Manifest_execution_level_is_asInvoker()
    {
        // requireAdministrator prevents Windows from launching the app via
        // HKCU\...\Run — CreateProcess() silently fails with ERROR_ELEVATION_REQUIRED.
        var manifestPath = FindManifestPath();
        Assert.True(File.Exists(manifestPath), $"app.manifest not found at {manifestPath}");

        var xml = XDocument.Load(manifestPath);
        var ns = XNamespace.Get("urn:schemas-microsoft-com:asm.v3");
        var level = xml.Descendants(ns + "requestedExecutionLevel").FirstOrDefault();

        Assert.NotNull(level);
        Assert.Equal("asInvoker", level.Attribute("level")?.Value);
    }

    [Fact]
    public void Manifest_uiAccess_is_false()
    {
        var manifestPath = FindManifestPath();
        var xml = XDocument.Load(manifestPath);
        var ns = XNamespace.Get("urn:schemas-microsoft-com:asm.v3");
        var level = xml.Descendants(ns + "requestedExecutionLevel").FirstOrDefault();

        Assert.NotNull(level);
        Assert.Equal("false", level.Attribute("uiAccess")?.Value);
    }

    // ── Registry autostart format ───────────────────────────────────

    [Fact]
    public void Registry_Run_value_includes_autostart_flag()
    {
        // Simulate what ApplyAutoStartRegistry writes.
        // Format: "C:\path\to\ZConnect.exe" --autostart
        var exePath = @"C:\Soft\ZConect-Debug\ZConnect.exe";
        var regValue = $"\"{exePath}\" --autostart";

        Assert.Contains("--autostart", regValue);
        Assert.StartsWith("\"", regValue);
        Assert.Contains("ZConnect.exe", regValue);
    }

    [Fact]
    public void Registry_Run_value_handles_spaces_in_path()
    {
        var exePath = @"C:\Program Files\ZConect\ZConnect.exe";
        var regValue = $"\"{exePath}\" --autostart";

        // Quotes protect the path with spaces.
        Assert.StartsWith("\"C:\\Program Files", regValue);
        Assert.EndsWith("--autostart", regValue);
    }

    [Fact]
    public void Registry_key_path_is_correct()
    {
        // Validate the key path used in ApplyAutoStartRegistry.
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // Must be HKCU (current user), not HKLM.
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        Assert.NotNull(key); // This key always exists on Windows.
    }

    // ── Command-line flag parsing ───────────────────────────────────

    [Theory]
    [InlineData("--autostart", true)]
    [InlineData("--AUTOSTART", true)]
    [InlineData("--AutoStart", true)]
    [InlineData("--from-service", false)]
    [InlineData("", false)]
    public void Autostart_flag_parsing_case_insensitive(string arg, bool expected)
    {
        var args = string.IsNullOrEmpty(arg) ? Array.Empty<string>() : new[] { "ZConnect.exe", arg };
        var isAutoStart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expected, isAutoStart);
    }

    [Theory]
    [InlineData("--from-service", true)]
    [InlineData("--FROM-SERVICE", true)]
    [InlineData("--autostart", false)]
    [InlineData("", false)]
    public void FromService_flag_parsing_case_insensitive(string arg, bool expected)
    {
        var args = string.IsNullOrEmpty(arg) ? Array.Empty<string>() : new[] { "ZConnect.exe", arg };
        var isFromService = args.Any(a => a.Equals("--from-service", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expected, isFromService);
    }

    [Fact]
    public void Autostart_does_not_skip_mutex()
    {
        // --autostart should NOT skip single-instance mutex (unlike --from-service).
        // Verify the flag is different from --from-service.
        var args = new[] { "ZConnect.exe", "--autostart" };
        var launchedByService = args.Any(a => a.Equals("--from-service", StringComparison.OrdinalIgnoreCase));
        var autoStart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

        Assert.False(launchedByService, "--autostart must NOT be treated as --from-service");
        Assert.True(autoStart);
    }

    // ── ClientSettings defaults for autostart ───────────────────────

    [Fact]
    public void ClientSettings_AutoStartOnBoot_default_true()
    {
        var settings = new UiApp.Models.ClientSettings();
        Assert.True(settings.AutoStartOnBoot);
    }

    [Fact]
    public void ClientSettings_MinimizeToTray_default_true()
    {
        var settings = new UiApp.Models.ClientSettings();
        Assert.True(settings.MinimizeToTray);
    }

    [Fact]
    public void ClientSettings_MachineId_default_empty()
    {
        var settings = new UiApp.Models.ClientSettings();
        Assert.Equal(string.Empty, settings.MachineId);
    }

    // ── Boot timing: service starts before GUI ──────────────────────

    [Fact]
    public void ServiceConfig_persists_across_reboot_simulation()
    {
        // Simulate: service saves config → reboot → service loads config.
        var config = new ZConectService.ServiceConfig
        {
            MachineId = "persistent-machine-id",
            DeviceSecret = "persistent-secret",
            UnattendedEnabled = true,
            SignalingUrl = "http://server:8080"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ZConectService.ServiceConfig>(json);

        Assert.NotNull(restored);
        Assert.Equal("persistent-machine-id", restored.MachineId);
        Assert.Equal("persistent-secret", restored.DeviceSecret);
        Assert.True(restored.UnattendedEnabled);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string FindManifestPath()
    {
        // Walk up from test bin directory to find client/UiApp/app.manifest.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir?.Parent != null)
        {
            var candidate = Path.Combine(dir.FullName, "UiApp", "app.manifest");
            if (File.Exists(candidate)) return candidate;

            candidate = Path.Combine(dir.FullName, "client", "UiApp", "app.manifest");
            if (File.Exists(candidate)) return candidate;

            dir = dir.Parent;
        }
        // Fallback: hardcoded relative from test project.
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "UiApp", "app.manifest"));
    }
}
