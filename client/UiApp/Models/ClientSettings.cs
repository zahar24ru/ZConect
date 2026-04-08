namespace UiApp.Models;

/// <summary>Вариант длительности сессии для выбора в UI (вкладка «Создать сессию», без подтверждения).</summary>
public sealed class SessionDurationOption
{
    public int Seconds { get; init; }
    public string Label { get; init; } = "";
}

public sealed class ClientSettings
{
    public string ServerApiBaseUrl { get; set; } = "http://127.0.0.1:8080";
    public string WebSocketUrl { get; set; } = "ws://127.0.0.1:8080/ws";
    public string StunUrl { get; set; } = "stun:127.0.0.1:3478";
    public string TurnUrl { get; set; } = "turn:127.0.0.1:3478";
    public string TurnUsername { get; set; } = "zconect";
    public string TurnPassword { get; set; } = "change_me";
    public bool PreferRelay { get; set; }
    public bool PreferLanVpnNoTurn { get; set; }
    public bool AutoIceByPriority { get; set; } = true;
    public bool RequireConfirmation { get; set; } = true;
    public bool AllowUnattended { get; set; }
    /// <summary>Длительность сессии в секундах при включённом «без подтверждения» (для адресной книги). По умолчанию 24 ч.</summary>
    public int UnattendedExpiresInSec { get; set; } = 86400;
public string QualityPreset { get; set; } = "Auto";
    public string DisplayId { get; set; } = "DISPLAY1";
    /// <summary>Запускать приложение при загрузке Windows.</summary>
    public bool AutoStartOnBoot { get; set; }
    /// <summary>Автоматически создавать сессию при запуске приложения.</summary>
    public bool AutoCreateSession { get; set; }
    /// <summary>Уникальный ID машины, генерируется при первом запуске.</summary>
    public string MachineId { get; set; } = string.Empty;

    /// <summary>Последний session_id (для переиспользования при перезапуске).</summary>
    public string LastSessionId { get; set; } = string.Empty;
    /// <summary>Последний login_code (для показа в UI до ответа сервера).</summary>
    public string LastLoginCode { get; set; } = string.Empty;
    /// <summary>Последний pass_code (для показа в UI до ответа сервера).</summary>
    public string LastPassCode { get; set; } = string.Empty;
    /// <summary>Время истечения последней сессии (UTC ticks). 0 = нет.</summary>
    public long LastSessionExpiresAtUtcTicks { get; set; }

    /// <summary>При нажатии крестика: true = свернуть в трей, false = закрыть приложение.</summary>
    public bool MinimizeToTray { get; set; } = true;

    public bool DebugLogDataChannelInputEnabled { get; set; } = true;
    public bool DebugLogClipboardEnabled { get; set; } = true;
    public bool DebugLogSignalingEnabled { get; set; } = true;
    public bool DebugLogWebRtcEnabled { get; set; } = true;
}
