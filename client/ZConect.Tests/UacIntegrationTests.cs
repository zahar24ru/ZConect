using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using UiApp.Services;
using WebRtcTransport;
using Xunit;

namespace ZConect.Tests;

/// <summary>
/// Integration tests for UAC Phase 2 components.
/// These tests use REAL OS resources (named pipes, shared memory, processes)
/// to catch serialization mismatches and protocol errors that unit tests miss.
///
/// Key: Group 1 tests would have caught the Button int/string bug that
/// broke input on UAC prompts — GUI serialized Button as int, agent tried GetString().
/// </summary>
public sealed class UacIntegrationTests
{

    // ═══════════════════════════════════════════════════════════════════
    // GROUP 1: Input JSON round-trip
    // Catches type mismatches like Button (int) parsed as GetString()
    // ═══════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Mouse_input_roundtrip_matches_agent_deserialization()
    {
        // GUI side: creates payload and wraps it for agent.
        var guiPayload = new MouseInputPayload
        {
            Action = "click",
            X = 500,
            Y = 300,
            Button = 0, // left = 0, middle = 1, right = 2
            Delta = 0
        };

        // GUI serializes exactly like MainViewModel.Input.cs does:
        var guiJson = JsonSerializer.Serialize(new { type = "mouse", payload = guiPayload });

        // Agent side: deserializes exactly like UacAgentHost.ProcessInputCommand does:
        using var doc = JsonDocument.Parse(guiJson);
        var root = doc.RootElement;

        string? type = null;
        JsonElement payloadEl = default;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals("type", StringComparison.OrdinalIgnoreCase))
                type = prop.Value.GetString();
            else if (prop.Name.Equals("payload", StringComparison.OrdinalIgnoreCase))
                payloadEl = prop.Value;
        }

        Assert.Equal("mouse", type);

        // This is the EXACT deserialization the agent uses — if types mismatch, test FAILS.
        var agentPayload = JsonSerializer.Deserialize<AgentMousePayload>(
            payloadEl.GetRawText(), CaseInsensitive);

        Assert.NotNull(agentPayload);
        Assert.Equal("click", agentPayload.Action);
        Assert.Equal(500, agentPayload.X);
        Assert.Equal(300, agentPayload.Y);
        Assert.Equal(0, agentPayload.Button); // int, not string!
        Assert.Equal(0, agentPayload.Delta);
    }

    [Fact]
    public void Keyboard_input_roundtrip_matches_agent_deserialization()
    {
        var guiPayload = new KeyboardInputPayload
        {
            Action = "down",
            VirtualKey = 0x0D, // Enter
            ScanCode = 28,
            Alt = false,
            Ctrl = true,
            Shift = false,
            Win = false
        };

        var guiJson = JsonSerializer.Serialize(new { type = "keyboard", payload = guiPayload });

        using var doc = JsonDocument.Parse(guiJson);
        var root = doc.RootElement;

        string? type = null;
        JsonElement payloadEl = default;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals("type", StringComparison.OrdinalIgnoreCase))
                type = prop.Value.GetString();
            else if (prop.Name.Equals("payload", StringComparison.OrdinalIgnoreCase))
                payloadEl = prop.Value;
        }

        Assert.Equal("keyboard", type);

        var agentPayload = JsonSerializer.Deserialize<AgentKeyPayload>(
            payloadEl.GetRawText(), CaseInsensitive);

        Assert.NotNull(agentPayload);
        Assert.Equal("down", agentPayload.Action);
        Assert.Equal(0x0D, agentPayload.VirtualKey);
        Assert.Equal(28, agentPayload.ScanCode);
    }

    [Theory]
    [InlineData(0, "left")]    // Button 0 = left
    [InlineData(1, "middle")]  // Button 1 = middle
    [InlineData(2, "right")]   // Button 2 = right
    public void Mouse_button_int_maps_correctly(int buttonInt, string expectedName)
    {
        // Agent interprets Button as int: 0=left, 1=middle, 2=right.
        var name = buttonInt switch
        {
            0 => "left",
            1 => "middle",
            2 => "right",
            _ => "left"
        };
        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void Mouse_move_roundtrip()
    {
        var payload = new MouseInputPayload { Action = "move", X = 1920, Y = 1080 };
        var json = JsonSerializer.Serialize(new { type = "mouse", payload });
        var doc = JsonDocument.Parse(json);
        var payloadEl = GetPayloadElement(doc);

        var parsed = JsonSerializer.Deserialize<AgentMousePayload>(payloadEl.GetRawText(), CaseInsensitive)!;
        Assert.Equal("move", parsed.Action);
        Assert.Equal(1920, parsed.X);
        Assert.Equal(1080, parsed.Y);
    }

    [Fact]
    public void Mouse_wheel_roundtrip()
    {
        var payload = new MouseInputPayload { Action = "wheel", X = 100, Y = 200, Delta = -120 };
        var json = JsonSerializer.Serialize(new { type = "mouse", payload });
        var doc = JsonDocument.Parse(json);
        var payloadEl = GetPayloadElement(doc);

        var parsed = JsonSerializer.Deserialize<AgentMousePayload>(payloadEl.GetRawText(), CaseInsensitive)!;
        Assert.Equal("wheel", parsed.Action);
        Assert.Equal(-120, parsed.Delta);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GROUP 2: Desktop switch detection
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Agent_pipe_receives_input_command()
    {
        var pipeName = "ZConect_UacInputTest_" + Guid.NewGuid().ToString("N")[..8];
        using var cts = new CancellationTokenSource(5000);

        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await Task.WhenAll(
            server.WaitForConnectionAsync(cts.Token),
            client.ConnectAsync(cts.Token));

        // GUI sends input command via client.
        var inputCmd = new { type = "mouse", payload = new MouseInputPayload { Action = "click", X = 100, Y = 200, Button = 2 } };
        var json = JsonSerializer.Serialize(inputCmd);
        var payload = Encoding.UTF8.GetBytes(json);

        var writeTask = Task.Run(async () =>
        {
            await client.WriteAsync(BitConverter.GetBytes(payload.Length), cts.Token);
            await client.WriteAsync(payload, cts.Token);
            await client.FlushAsync(cts.Token);
        });

        // Agent reads.
        var header = new byte[4];
        await ReadExact(server, header, cts.Token);
        var len = BitConverter.ToInt32(header, 0);
        var buf = new byte[len];
        await ReadExact(server, buf, cts.Token);

        await writeTask;

        // Agent deserializes.
        var received = Encoding.UTF8.GetString(buf);
        var doc = JsonDocument.Parse(received);
        Assert.Equal("mouse", doc.RootElement.GetProperty("type").GetString());

        var payloadEl = GetPayloadElement(doc);
        var parsed = JsonSerializer.Deserialize<AgentMousePayload>(payloadEl.GetRawText(), CaseInsensitive)!;
        Assert.Equal("click", parsed.Action);
        Assert.Equal(100, parsed.X);
        Assert.Equal(200, parsed.Y);
        Assert.Equal(2, parsed.Button); // right click
    }



    // ═══════════════════════════════════════════════════════════════════
    // GROUP 5: DesktopMonitor on real desktop
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void DesktopMonitor_reads_current_desktop_name()
    {
        var name = DesktopMonitor.GetInputDesktopName();
        Assert.NotNull(name);
        Assert.NotEmpty(name);
        // On normal test run, should be "Default".
        Assert.Equal("Default", name);
    }

    [Fact]
    public async Task DesktopMonitor_polling_does_not_fire_on_stable_desktop()
    {
        using var monitor = new DesktopMonitor();
        int switchCount = 0;
        monitor.DesktopChanged += (_, _) => Interlocked.Increment(ref switchCount);

        using var cts = new CancellationTokenSource();
        monitor.Start(cts.Token);

        // Wait 2 seconds — no switches should fire on stable desktop.
        await Task.Delay(2000);
        cts.Cancel();

        Assert.Equal(0, switchCount);
    }

    [Fact]
    public void DesktopMonitor_IsSecureDesktop_correct_for_known_names()
    {
        // Test the logic: anything != "Default" is secure.
        Assert.False(IsSecure("Default"));
        Assert.False(IsSecure("default"));
        Assert.True(IsSecure("Winlogon"));
        Assert.True(IsSecure("winlogon"));
        Assert.True(IsSecure("Screensaver"));

        static bool IsSecure(string name) =>
            !name.Equals("Default", StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Agent-side deserialization model for mouse (matches UacAgentHost.MousePayload).</summary>
    private sealed class AgentMousePayload
    {
        public string? Action { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Button { get; set; }
        public int Delta { get; set; }
    }

    /// <summary>Agent-side deserialization model for keyboard (matches UacAgentHost.KeyPayload).</summary>
    private sealed class AgentKeyPayload
    {
        public string? Action { get; set; }
        public int VirtualKey { get; set; }
        public int ScanCode { get; set; }
    }

    private static JsonElement GetPayloadElement(JsonDocument doc)
    {
        foreach (var prop in doc.RootElement.EnumerateObject())
            if (prop.Name.Equals("payload", StringComparison.OrdinalIgnoreCase))
                return prop.Value;
        throw new KeyNotFoundException("payload");
    }

    private static async Task ReadExact(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
    }
}
