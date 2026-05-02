namespace UiApp.Models;

/// <summary>Вариант длительности сессии для выбора в UI (вкладка «Создать сессию», без подтверждения).</summary>
public sealed class SessionDurationOption
{
    public int Seconds { get; init; }
    public string Label { get; init; } = "";
}

public sealed class WindowRect
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}

public sealed class ClientSettings
{
    /// <summary>
    /// Base URL сервера — принимает https:// (prod Caddy+Let's Encrypt) или http://.
    /// Когда задан https://, WebSocket URL (ResolveWsUrl в MainViewModel.Connection.cs)
    /// автоматически использует wss://. WPF HttpClient поддерживает HTTPS out-of-the-box.
    ///
    /// Default updated 2026-04-24: был "http://92.63.102.244:8080" (direct HTTP на port
    /// 8080), но после HTTPS deployment (Caddy + Let's Encrypt) сервер больше не exposes
    /// 8080 наружу — только 443 через domain. Legacy installs auto-migrate в SettingsService.Load
    /// (ищет устаревший URL и upgrade'ит).
    /// </summary>
    public string ServerApiBaseUrl { get; set; } = "https://connect.zconn.ru";
    public string WebSocketUrl { get; set; } = "wss://connect.zconn.ru/ws";
    public string StunUrl { get; set; } = "stun:92.63.102.244:3478";
    public string TurnUrl { get; set; } = "turn:92.63.102.244:3478";
    public string TurnUsername { get; set; } = "zconect";
    // F-02 (external audit 2026-04-18): default был hardcoded "123456" —
    // installer не перезаписывает настройки пользователя, любой получавший
    // свежий client-settings получал эту дефолтную password → мог использовать
    // TURN relay анонимно. Сейчас empty; реальное значение приходит через
    // service pipe config message или UI Settings (пользователь вводит сам).
    public string TurnPassword { get; set; } = string.Empty;
    /// <summary>Additional TURN servers для failover. Primary TurnUrl/TurnUsername/TurnPassword
    /// используется как первая запись; этот список — fallback'и. Empty list = один primary server.</summary>
    public List<TurnServerSetting> TurnServers { get; set; } = new();
    /// <summary>Force all traffic через TURN relay (IceTransportType.Relay). Для диагностики или
    /// когда user явно хочет скрыть IP от peer'а. Default false — mrwebrtc gathers host/srflx/relay.</summary>
    public bool PreferRelay { get; set; }
    /// <summary>Не включать TURN server в ICE config — оставить только STUN/host candidates.
    /// Использовать только на trusted LAN/VPN где TURN не нужен и создаёт ненужный hop.</summary>
    public bool PreferLanVpnNoTurn { get; set; }
    public bool AutoIceByPriority { get; set; } = true;
    public bool RequireConfirmation { get; set; } = true;
    public bool AllowUnattended { get; set; }
    /// <summary>Длительность сессии в секундах при включённом «без подтверждения» (для адресной книги). По умолчанию 24 ч.</summary>
    public int UnattendedExpiresInSec { get; set; } = 86400;

    /// <summary>PBKDF2-SHA256 hash (Base64) локального пароля для unattended auto-approve.
    /// Если пустой — AllowUnattended trakted как выключенный, viewer всегда идёт через confirmation dialog.
    /// Шифруется DPAPI через SecretFields (см. SettingsService).</summary>
    public string UnattendedPasswordHash { get; set; } = string.Empty;

    /// <summary>Salt (Base64, 16 random bytes) для PBKDF2-derivation unattended password.
    /// Генерится один раз вместе с hash при SetPassword. Шифруется DPAPI.</summary>
    public string UnattendedPasswordSalt { get; set; } = string.Empty;

    /// <summary>Кол-во подряд неверных попыток auth. Монотонно растёт, сбрасывается только на successful auth
    /// или SetPassword/ClearPassword. Каждые 3 fail'а → следующий LockoutTier.</summary>
    public int UnattendedFailedAttempts { get; set; }

    /// <summary>Текущий lockout tier (0 — никогда не срабатывал, 1-4+ — растёт после каждого 3-го fail'а).
    /// Определяет duration следующего lockout (1/5/15/60 мин). Cap на tier 4.</summary>
    public int UnattendedLockoutTier { get; set; }

    /// <summary>UTC timestamp до которого любой verify возвращает LockedOut. MinValue = не locked.
    /// Сохраняется в settings — lockout соблюдается после restart.</summary>
    public DateTime UnattendedLockoutUntilUtc { get; set; } = DateTime.MinValue;
public string QualityPreset { get; set; } = "Auto";
    public string DisplayId { get; set; } = "DISPLAY1";
    /// <summary>Запускать приложение при загрузке Windows (по умолчанию — да).</summary>
    public bool AutoStartOnBoot { get; set; } = true;
    /// <summary>Автоматически создавать сессию при запуске приложения.</summary>
    public bool AutoCreateSession { get; set; } = true;
    /// <summary>Уникальный ID машины, генерируется при первом запуске.</summary>
    public string MachineId { get; set; } = string.Empty;

    /// <summary>Device secret — proof of device ownership for session reuse (NET-03).
    /// Adopted from service via pipe if running under service, otherwise persisted from first CreateSession response.</summary>
    public string DeviceSecret { get; set; } = string.Empty;

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

    /// <summary>Тема интерфейса: Light, Dark.</summary>
    public string ThemeMode { get; set; } = "Light";

    /// <summary>Root-jail for remote FT operations (file requests, create/delete). Empty = user Downloads folder.</summary>
    public string FtRootJailPath { get; set; } = string.Empty;

    public bool DebugLogDataChannelInputEnabled { get; set; } = true;
    public bool DebugLogClipboardEnabled { get; set; } = true;
    public bool DebugLogSignalingEnabled { get; set; } = true;
    public bool DebugLogWebRtcEnabled { get; set; } = true;

    public WindowRect MainWindowRect { get; set; } = new();
    public WindowRect SettingsWindowRect { get; set; } = new();
    public WindowRect FileTransferWindowRect { get; set; } = new();

    /// <summary>Set to true после того как user пройдёт onboarding wizard при первом
    /// запуске (или нажмёт «Пропустить»). Overlay больше не показывается.</summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>Timestamp подтверждения ознакомления с Политикой конфиденциальности
    /// (https://zconn.ru/privacy_win.html). Заполняется когда user поставил чекбокс
    /// «Я ознакомился с политикой» на 4-м шаге onboarding'а. Soft variant — чекбокс
    /// не обязателен для прохождения wizard'а, но если поставлен — служит audit-записью
    /// факта получения согласия (152-ФЗ ч.1 ст.9: согласие может фиксироваться в
    /// электронной форме).</summary>
    public DateTime? PrivacyPolicyAckedAtUtc { get; set; }

    /// <summary>Включает проверку новой версии через /api/v1/client/version.
    /// Default true; user может отключить в Settings → Приложение.</summary>
    public bool AutoUpdateCheckEnabled { get; set; } = true;

    /// <summary>Viewer-side: показывать quality overlay (FPS/bitrate/RTT) в
    /// RemoteScreenWindow. Toggle через Ctrl+I. Default true.</summary>
    public bool ShowQualityOverlay { get; set; } = true;

    /// <summary>UI language code ("ru-RU" / "en-US"). Empty = system default.
    /// Применяется в App.xaml.cs OnStartup через CultureInfo.CurrentUICulture.
    /// Для применения требуется restart приложения.</summary>
    public string UiLanguage { get; set; } = string.Empty;
}

public sealed class TurnServerSetting
{
    public string Url { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
