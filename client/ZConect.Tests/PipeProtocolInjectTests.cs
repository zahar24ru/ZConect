using System.Text.Json;
using Xunit;
using ZConectService;

namespace ZConect.Tests;

/// <summary>
/// Round-trip сериализации pipe messages для input injection delegation.
/// После Stage 2 (0fef616) UI шлёт mouse/keyboard/launch_process через pipe
/// в service. Любая регрессия в JSON property names или field mapping
/// молча сломает input — убедимся что формат стабильный.
/// </summary>
public sealed class PipeProtocolInjectTests
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void InjectMouse_move_roundtrip()
    {
        var wire = JsonSerializer.Serialize(new
        {
            type = "inject_mouse",
            inputAction = "move",
            inputX = 150,
            inputY = 300,
            inputButton = 0,
            inputDelta = 0,
        }, CamelCase);

        var msg = JsonSerializer.Deserialize<PipeMessage>(wire, CaseInsensitive);

        Assert.NotNull(msg);
        Assert.Equal("inject_mouse", msg!.Type);
        Assert.Equal("move", msg.InputAction);
        Assert.Equal(150, msg.InputX);
        Assert.Equal(300, msg.InputY);
        Assert.Equal(0, msg.InputButton);
    }

    [Fact]
    public void InjectMouse_click_with_button_roundtrip()
    {
        var wire = JsonSerializer.Serialize(new
        {
            type = "inject_mouse",
            inputAction = "click",
            inputX = 500,
            inputY = 400,
            inputButton = 2, // right click
            inputDelta = 0,
        }, CamelCase);

        var msg = JsonSerializer.Deserialize<PipeMessage>(wire, CaseInsensitive);

        Assert.NotNull(msg);
        Assert.Equal("click", msg!.InputAction);
        Assert.Equal(2, msg.InputButton);
    }

    [Fact]
    public void InjectMouse_wheel_carries_delta()
    {
        var wire = JsonSerializer.Serialize(new
        {
            type = "inject_mouse",
            inputAction = "wheel",
            inputX = 500, inputY = 400,
            inputButton = 0,
            inputDelta = -120,  // scroll down
        }, CamelCase);

        var msg = JsonSerializer.Deserialize<PipeMessage>(wire, CaseInsensitive);

        Assert.NotNull(msg);
        Assert.Equal("wheel", msg!.InputAction);
        Assert.Equal(-120, msg.InputDelta);
    }

    [Fact]
    public void InjectKeyboard_down_with_modifiers_roundtrip()
    {
        var wire = JsonSerializer.Serialize(new
        {
            type = "inject_keyboard",
            inputAction = "down",
            inputVirtualKey = 0x41, // 'A'
            inputScanCode = 0x1E,
            inputAlt = false,
            inputCtrl = true,
            inputShift = true,
            inputWin = false,
        }, CamelCase);

        var msg = JsonSerializer.Deserialize<PipeMessage>(wire, CaseInsensitive);

        Assert.NotNull(msg);
        Assert.Equal("inject_keyboard", msg!.Type);
        Assert.Equal("down", msg.InputAction);
        Assert.Equal(0x41, msg.InputVirtualKey);
        Assert.Equal(0x1E, msg.InputScanCode);
        Assert.True(msg.InputCtrl);
        Assert.True(msg.InputShift);
        Assert.False(msg.InputAlt);
        Assert.False(msg.InputWin);
    }

    [Fact]
    public void PipeMessage_unknown_type_does_not_crash_deserializer()
    {
        // Защита: если в будущем появится новый type — старая service-версия
        // не должна падать, просто игнорировать в default case switch'а.
        var wire = JsonSerializer.Serialize(new { type = "some_future_message", inputX = 42 }, CamelCase);
        var msg = JsonSerializer.Deserialize<PipeMessage>(wire, CaseInsensitive);

        Assert.NotNull(msg);
        Assert.Equal("some_future_message", msg!.Type);
        Assert.Equal(42, msg.InputX);
    }

    [Fact]
    public void PipeMessage_partial_fields_deserialize_defaults()
    {
        // Сервис-handler должен нормально обработать message с только type и action,
        // без X/Y/button — всё равно не крашится (inject_move с 0,0 координатами ок).
        var wire = JsonSerializer.Serialize(new { type = "inject_mouse", inputAction = "move" }, CamelCase);
        var msg = JsonSerializer.Deserialize<PipeMessage>(wire, CaseInsensitive);

        Assert.NotNull(msg);
        Assert.Equal("move", msg!.InputAction);
        Assert.Equal(0, msg.InputX);
        Assert.Equal(0, msg.InputY);
    }
}
