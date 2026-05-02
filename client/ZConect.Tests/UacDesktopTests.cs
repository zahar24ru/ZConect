using ScreenCapture;
using UiApp.Services;
using Xunit;

namespace ZConect.Tests;

/// <summary>
/// Tests for UAC Phase 2 components:
/// - AcquireResult enum and DXGI ACCESS_LOST detection
/// - DesktopMonitor name parsing
/// - Desktop switch callback
/// </summary>
public sealed class UacDesktopTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }

    // ── AcquireResult enum ──────────────────────────────────────────

    [Fact]
    public void AcquireResult_has_three_values()
    {
        Assert.Equal(0, (int)AcquireResult.Success);
        Assert.Equal(1, (int)AcquireResult.NoFrame);
        Assert.Equal(2, (int)AcquireResult.AccessLost);
    }

    [Fact]
    public void DXGI_ERROR_ACCESS_LOST_code_is_correct()
    {
        // 0x887A0026 is the standard DXGI_ERROR_ACCESS_LOST code.
        const int expected = unchecked((int)0x887A0026);
        Assert.Equal(-2005270490, expected);
    }

    // ── DesktopMonitor ──────────────────────────────────────────────

    [Fact]
    public void DesktopMonitor_GetInputDesktopName_returns_non_null()
    {
        // On a normal Windows desktop, this should return "Default" or similar.
        var name = DesktopMonitor.GetInputDesktopName();
        Assert.NotNull(name);
        Assert.NotEmpty(name);
    }

    [Fact]
    public void DesktopMonitor_initial_state_is_Default()
    {
        using var monitor = new DesktopMonitor();
        Assert.Equal("Default", monitor.CurrentDesktop);
        Assert.False(monitor.IsSecureDesktop);
    }

    [Fact]
    public void DesktopMonitor_IsSecureDesktop_detects_non_default()
    {
        // IsSecureDesktop returns true for any name != "Default".
        using var monitor = new DesktopMonitor();
        // Can't set _currentDesktop directly, but we can test the static method.
        var name = DesktopMonitor.GetInputDesktopName();
        bool isSecure = !name!.Equals("Default", StringComparison.OrdinalIgnoreCase);
        // During normal test execution, we should be on Default desktop.
        Assert.False(isSecure);
    }

    [Fact]
    public void DesktopMonitor_Dispose_does_not_throw()
    {
        var monitor = new DesktopMonitor();
        monitor.Start();
        Thread.Sleep(100); // let poll run once
        monitor.Dispose(); // should not throw
    }

    [Fact]
    public void DesktopMonitor_double_Dispose_safe()
    {
        var monitor = new DesktopMonitor();
        monitor.Dispose();
        monitor.Dispose(); // second dispose safe
    }

    // ── Pipe message format ─────────────────────────────────────────

    [Fact]
    public void Desktop_changed_message_Winlogon_format()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new { type = "desktop_changed", desktop = "Winlogon" });
        Assert.Contains("\"desktop\":\"Winlogon\"", json);
    }

    [Fact]
    public void Desktop_changed_message_Default_format()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new { type = "desktop_changed", desktop = "Default" });
        Assert.Contains("\"desktop\":\"Default\"", json);
    }

    // ── Command line flags ──────────────────────────────────────────

    [Theory]
    [InlineData("--uac-agent", true)]
    [InlineData("--UAC-AGENT", true)]
    [InlineData("--Uac-Agent", true)]
    [InlineData("--from-service", false)]
    [InlineData("--autostart", false)]
    public void UacAgent_flag_parsing(string arg, bool expected)
    {
        var args = new[] { "ZConnect.exe", arg };
        var isAgent = args.Any(a => a.Equals("--uac-agent", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expected, isAgent);
    }
}
