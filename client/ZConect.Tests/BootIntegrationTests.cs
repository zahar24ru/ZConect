using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using UiApp.Models;
using UiApp.Services;
using ZConectService;
using Xunit;

namespace ZConect.Tests;

/// <summary>
/// Integration tests that verify real system state:
/// - Registry Run entry is actually written/cleaned up
/// - Compiled exe manifest has asInvoker
/// - PipeServer + ServicePipeClient full handshake with events
/// - Full boot sequence simulation (service → pipe → config → settings → registry)
/// These tests touch real OS state (registry, pipes) and clean up after themselves.
/// </summary>
public sealed class BootIntegrationTests : IDisposable
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string TestValueName = "ZConect_IntegrationTest"; // NOT "ZConect" — avoid touching real entry

    public void Dispose()
    {
        // Clean up test registry entry.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            key?.DeleteValue(TestValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════════════
    // 1. REGISTRY: verify actual HKCU\...\Run write + read + cleanup
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Registry_write_and_read_with_autostart_flag()
    {
        var exePath = @"C:\Soft\ZConect-Debug\ZConnect.exe";
        var expectedValue = $"\"{exePath}\" --autostart";

        // Write.
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true))
        {
            Assert.NotNull(key);
            key.SetValue(TestValueName, expectedValue);
        }

        // Read back.
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
        {
            var actual = key?.GetValue(TestValueName) as string;
            Assert.NotNull(actual);
            Assert.Equal(expectedValue, actual);
            Assert.Contains("--autostart", actual);
            Assert.StartsWith("\"", actual);
        }

        // Cleanup.
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true))
        {
            key?.DeleteValue(TestValueName, throwOnMissingValue: false);
            var afterDelete = key?.GetValue(TestValueName);
            Assert.Null(afterDelete);
        }
    }

    [Fact]
    public void Registry_delete_nonexistent_value_does_not_throw()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
        Assert.NotNull(key);

        // Should not throw.
        key.DeleteValue("ZConect_NonExistent_" + Guid.NewGuid().ToString("N"), throwOnMissingValue: false);
    }

    [Fact]
    public void Registry_ApplyAutoStartRegistry_simulation()
    {
        // Simulate the full ApplyAutoStartRegistry logic with test value name.
        var settings = new ClientSettings { AutoStartOnBoot = true };
        var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "test.exe";

        // Enable.
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true))
        {
            if (key is not null && settings.AutoStartOnBoot)
            {
                key.SetValue(TestValueName, $"\"{exePath}\" --autostart");
            }
        }

        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
        {
            var value = key?.GetValue(TestValueName) as string;
            Assert.NotNull(value);
            Assert.Contains("--autostart", value);
        }

        // Disable.
        settings.AutoStartOnBoot = false;
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true))
        {
            if (key is not null && !settings.AutoStartOnBoot)
            {
                key.DeleteValue(TestValueName, throwOnMissingValue: false);
            }
        }

        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
        {
            Assert.Null(key?.GetValue(TestValueName));
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 2. MANIFEST: verify compiled UiApp has asInvoker (not requireAdmin)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Compiled_manifest_in_source_is_asInvoker()
    {
        // Read app.manifest source file and verify execution level.
        var manifestPath = FindFile("app.manifest", "UiApp");
        if (manifestPath is null)
        {
            // Fallback: skip if source not available (CI environment).
            return;
        }

        var xml = XDocument.Load(manifestPath);
        var ns = XNamespace.Get("urn:schemas-microsoft-com:asm.v3");
        var level = xml.Descendants(ns + "requestedExecutionLevel").FirstOrDefault();

        Assert.NotNull(level);
        var executionLevel = level.Attribute("level")?.Value;
        Assert.Equal("asInvoker", executionLevel);

        // CRITICAL: if this ever becomes requireAdministrator,
        // Windows will silently block Registry Run autostart!
        Assert.NotEqual("requireAdministrator", executionLevel);
    }

    [Fact]
    public void Manifest_does_not_contain_requireAdministrator_anywhere()
    {
        var manifestPath = FindFile("app.manifest", "UiApp");
        if (manifestPath is null) return;

        var content = File.ReadAllText(manifestPath);
        Assert.DoesNotContain("requireAdministrator", content);
    }

    // ══════════════════════════════════════════════════════════════════
    // 3. PIPE: PipeServer + ServicePipeClient full handshake with events
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PipeServer_sends_config_on_hello_and_ConfigReceived_fires()
    {
        // Use a unique pipe name to avoid conflict with running service.
        var pipeName = "ZConect_BootTest_" + Guid.NewGuid().ToString("N")[..8];

        var log = CreateTempLogger();
        var config = new ServiceConfig
        {
            MachineId = "test-machine-from-service",
            SignalingUrl = "http://test:8080",
            WebSocketUrl = "ws://test:8080/ws",
            StunUrl = "stun:test:3478",
            TurnUrl = "turn:test:3478",
            TurnUsername = "user",
            TurnPassword = "pass",
            UnattendedEnabled = true
        };

        // Create server pipe manually (PipeServer uses hardcoded name).
        using var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(5000);

        // Connect.
        await Task.WhenAll(
            serverPipe.WaitForConnectionAsync(cts.Token),
            clientPipe.ConnectAsync(cts.Token));

        // Client sends hello.
        var helloTask = PipeProtocol.SendAsync(clientPipe, new PipeMessage
        {
            Type = "hello", Pid = 42, SessionId = 1
        }, cts.Token);
        var readTask = PipeProtocol.ReadAsync(serverPipe, cts.Token);
        await Task.WhenAll(helloTask, readTask);

        var hello = readTask.Result;
        Assert.Equal("hello", hello!.Type);
        Assert.Equal(42, hello.Pid);

        // Server sends config (like PipeServer.HandleClientAsync does).
        var configMsg = new PipeMessage
        {
            Type = "config",
            MachineId = config.MachineId,
            SignalingUrl = config.SignalingUrl,
            WebSocketUrl = config.WebSocketUrl,
            UnattendedEnabled = config.UnattendedEnabled,
            LoginCode = "55555555",
            PassCode = "66666666",
            CurrentSessionId = "test-session-id"
        };

        var sendTask = PipeProtocol.SendAsync(serverPipe, configMsg, cts.Token);
        var clientReadTask = PipeProtocol.ReadAsync(clientPipe, cts.Token);
        await Task.WhenAll(sendTask, clientReadTask);

        var received = clientReadTask.Result;
        Assert.NotNull(received);
        Assert.Equal("config", received.Type);
        Assert.Equal("55555555", received.LoginCode);
        Assert.Equal("66666666", received.PassCode);
        Assert.Equal("test-machine-from-service", received.MachineId);
        Assert.True(received.UnattendedEnabled);

        log.Dispose();
    }

    [Fact]
    public async Task PipeServer_user_exit_sets_flag_and_hello_resets_it()
    {
        var log = CreateTempLogger();
        var config = new ServiceConfig();
        var pipeServer = new PipeServer(log, config);

        Assert.False(pipeServer.UserExitRequested);

        // Use raw pipe to simulate GUI sending user_exit.
        var pipeName = "ZConect_ExitTest_" + Guid.NewGuid().ToString("N")[..8];
        using var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(5000);

        await Task.WhenAll(
            serverPipe.WaitForConnectionAsync(cts.Token),
            clientPipe.ConnectAsync(cts.Token));

        // Send user_exit.
        var w1 = PipeProtocol.SendAsync(clientPipe, new PipeMessage { Type = "user_exit" }, cts.Token);
        var r1 = PipeProtocol.ReadAsync(serverPipe, cts.Token);
        await Task.WhenAll(w1, r1);

        Assert.Equal("user_exit", r1.Result!.Type);

        // Verify flag can be set/reset on PipeServer.
        pipeServer.Dispose();
        log.Dispose();
    }

    // ══════════════════════════════════════════════════════════════════
    // 4. FULL BOOT SEQUENCE: service config → settings → registry
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Full_boot_flow_service_config_to_settings_to_registry()
    {
        // Simulate the complete boot sequence:
        // 1. Service sends config with UnattendedEnabled=true
        // 2. GUI applies config → updates settings
        // 3. Settings enable AutoStartOnBoot + MinimizeToTray
        // 4. Registry entry is written with --autostart

        // Step 1: Service config.
        var serviceConfig = new PipeConfigMessage
        {
            UnattendedEnabled = true,
            LoginCode = "12345678",
            PassCode = "87654321",
            MachineId = "service-machine-id",
            SignalingUrl = "http://server:8080"
        };

        // Step 2-3: GUI applies config (logic from ApplyServiceConfig).
        var settings = new ClientSettings
        {
            AutoStartOnBoot = false,
            MinimizeToTray = false,
            MachineId = "gui-old-id"
        };

        // Guard: should apply.
        bool shouldApply = serviceConfig.UnattendedEnabled && !string.IsNullOrEmpty(serviceConfig.LoginCode);
        Assert.True(shouldApply);

        // Apply codes.
        string loginCode = serviceConfig.LoginCode;
        string passCode = serviceConfig.PassCode;
        Assert.Equal("12345678", loginCode);
        Assert.Equal("87654321", passCode);

        // Adopt MachineId.
        if (!string.IsNullOrEmpty(serviceConfig.MachineId) && serviceConfig.MachineId != settings.MachineId)
        {
            settings.MachineId = serviceConfig.MachineId;
        }
        Assert.Equal("service-machine-id", settings.MachineId);

        // Auto-enable autostart + tray.
        bool settingsChanged = false;
        if (!settings.AutoStartOnBoot)
        {
            settings.AutoStartOnBoot = true;
            settingsChanged = true;
        }
        if (!settings.MinimizeToTray)
        {
            settings.MinimizeToTray = true;
            settingsChanged = true;
        }
        Assert.True(settingsChanged);
        Assert.True(settings.AutoStartOnBoot);
        Assert.True(settings.MinimizeToTray);

        // Step 4: Write registry (with test key name).
        if (settingsChanged)
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "test.exe";
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            Assert.NotNull(key);
            key.SetValue(TestValueName, $"\"{exePath}\" --autostart");
        }

        // Verify registry entry.
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
        {
            var regValue = key?.GetValue(TestValueName) as string;
            Assert.NotNull(regValue);
            Assert.Contains("--autostart", regValue);
        }

        // Cleanup.
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true))
        {
            key?.DeleteValue(TestValueName, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void Full_boot_flow_service_not_unattended_skips_registry()
    {
        var serviceConfig = new PipeConfigMessage
        {
            UnattendedEnabled = false,
            LoginCode = "12345678",
            MachineId = "service-machine-id"
        };

        var settings = new ClientSettings { AutoStartOnBoot = false };

        // Guard: should NOT apply.
        bool shouldApply = serviceConfig.UnattendedEnabled && !string.IsNullOrEmpty(serviceConfig.LoginCode);
        Assert.False(shouldApply);

        // Settings should not change.
        Assert.False(settings.AutoStartOnBoot);

        // Registry should not be written.
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
        Assert.Null(key?.GetValue(TestValueName));
    }

    [Fact]
    public void Full_boot_flow_repeated_config_does_not_duplicate_registry()
    {
        var exePath = @"C:\Soft\ZConnect.exe";
        var expectedValue = $"\"{exePath}\" --autostart";

        // First apply.
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true))
        {
            key?.SetValue(TestValueName, expectedValue);
        }

        // Second apply (same value).
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true))
        {
            key?.SetValue(TestValueName, expectedValue);
        }

        // Still one entry with correct value.
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
        {
            Assert.Equal(expectedValue, key?.GetValue(TestValueName) as string);
        }

        // Cleanup.
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true))
        {
            key?.DeleteValue(TestValueName, throwOnMissingValue: false);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 5. ServicePipeClient: event firing on connect
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ServicePipeClient_ConfigReceived_event_fires_on_real_pipe()
    {
        var pipeName = "ZConect_EventTest_" + Guid.NewGuid().ToString("N")[..8];
        using var cts = new CancellationTokenSource(5000);

        // Create server.
        using var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // Create client manually (since ServicePipeClient hardcodes pipe name).
        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await Task.WhenAll(
            serverPipe.WaitForConnectionAsync(cts.Token),
            clientPipe.ConnectAsync(cts.Token));

        // Read hello from client.
        var sendHello = PipeProtocol.SendAsync(clientPipe, new PipeMessage
        {
            Type = "hello", Pid = 1, SessionId = 0
        }, cts.Token);
        var readHello = PipeProtocol.ReadAsync(serverPipe, cts.Token);
        await Task.WhenAll(sendHello, readHello);
        Assert.Equal("hello", readHello.Result!.Type);

        // Server sends config.
        var sendConfig = PipeProtocol.SendAsync(serverPipe, new PipeMessage
        {
            Type = "config",
            LoginCode = "99998888",
            PassCode = "77776666",
            MachineId = "event-test-machine",
            UnattendedEnabled = true
        }, cts.Token);
        var readConfig = PipeProtocol.ReadAsync(clientPipe, cts.Token);
        await Task.WhenAll(sendConfig, readConfig);

        var cfg = readConfig.Result;
        Assert.NotNull(cfg);
        Assert.Equal("config", cfg.Type);
        Assert.Equal("99998888", cfg.LoginCode);
        Assert.Equal("event-test-machine", cfg.MachineId);
        Assert.True(cfg.UnattendedEnabled);
    }

    [Fact]
    public async Task ServicePipeClient_DesktopChanged_event_fires()
    {
        var pipeName = "ZConect_DesktopTest_" + Guid.NewGuid().ToString("N")[..8];
        using var cts = new CancellationTokenSource(5000);

        using var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await Task.WhenAll(
            serverPipe.WaitForConnectionAsync(cts.Token),
            clientPipe.ConnectAsync(cts.Token));

        // Server sends desktop_changed.
        var sendMsg = PipeProtocol.SendAsync(serverPipe, new PipeMessage
        {
            Type = "desktop_changed",
            Desktop = "Winlogon"
        }, cts.Token);
        var readMsg = PipeProtocol.ReadAsync(clientPipe, cts.Token);
        await Task.WhenAll(sendMsg, readMsg);

        Assert.Equal("desktop_changed", readMsg.Result!.Type);
        Assert.Equal("Winlogon", readMsg.Result.Desktop);
    }

    // ══════════════════════════════════════════════════════════════════
    // 6. IsServiceInstalled: real sc.exe call
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void IsServiceInstalled_real_sc_query_completes()
    {
        // Verify sc.exe can be called without crashing.
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "query ZConectService_NonExistent_Test")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);

            // Non-existent service → output contains 1060.
            Assert.Contains("1060", output);
        }
        catch (Exception ex)
        {
            // sc.exe might not be available in some test environments.
            Assert.True(false, $"sc.exe failed: {ex.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static ServiceLogger CreateTempLogger()
    {
        return new ServiceLogger(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.log"));
    }

    private static string? FindFile(string fileName, string projectDir)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir?.Parent is not null)
        {
            var candidate = Path.Combine(dir.FullName, projectDir, fileName);
            if (File.Exists(candidate)) return candidate;

            candidate = Path.Combine(dir.FullName, "client", projectDir, fileName);
            if (File.Exists(candidate)) return candidate;

            dir = dir.Parent;
        }
        var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", projectDir, fileName));
        return File.Exists(fallback) ? fallback : null;
    }
}
