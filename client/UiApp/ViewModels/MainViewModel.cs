using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileTransfer;
using Microsoft.Win32;
using QualityController;
using ScreenCapture;
using SessionClient;
using UiApp.Models;
using UiApp.Properties;
using UiApp.Services;
using WebRtcTransport;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private const int ClipboardMaxBytes = 256 * 1024;
    private const int ClipboardPollMs = 250;
    private const string UacPolicyRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string UacPromptOnSecureDesktopValueName = "PromptOnSecureDesktop";

    private enum ConnectionRole
    {
        None,
        Host,
        Viewer
    }

    // Helper для создания frozen brushes — thread-safe, immutable.
    // Crash fix 2026-04-26: ArgumentException "DependencySource в том же потоке"
    // когда Brush создан на background thread и потом set'ится в DependencyProperty
    // через _uiContext.Post. Frozen brushes обходят thread affinity check.
    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    // Cached frozen brushes для common viewer/connection states (allocate once, reuse).
    private static readonly Brush BrushAmber = FrozenBrush(245, 158, 11);
    private static readonly Brush BrushGreen = FrozenBrush(34, 197, 94);
    private static readonly Brush BrushRed   = FrozenBrush(239, 68, 68);
    private static readonly Brush BrushGray  = FrozenBrush(156, 163, 175);

    private readonly SettingsService _settingsService;
    private readonly LogService _logService;
    private readonly SessionApiClient _sessionApiClient;
    private readonly AddressBookService _addressBookService;
    private readonly RecentHistoryService _recentHistoryService;
    private Services.ServicePipeClient? _servicePipe;
    private Services.DesktopMonitor? _desktopMonitor;
    private WebSocketSignalingClient? _signalingClient;
    private SignalingCoordinator? _signalingCoordinator;
    private DataChannelCoordinator? _dataChannelCoordinator;
    private MixedRealityPeerConnectionAgent? _realPeerAgent;
    /// <summary>Pre-created peer+data agents (built during pipe wait to overlap with network I/O).</summary>
    private Task<(IPeerConnectionAgent?, IDataChannelAgent?)>? _preCreatedAgentsTask;
    private ClientSettings _settings;
    private readonly SynchronizationContext _uiContext;
    private readonly WindowsInputInjectionService _windowsInputInjectionService;
    private CancellationTokenSource _lifetimeCts = new();
    private WriteableBitmap? _remoteFrameBitmap;
    private ScreenMetaPayload? _remoteScreenMeta;
    private ScreenMetaPayload? _localScreenMeta;
    private bool _mouseInputBlockedWarnedNoMeta;

    /// <summary>
    /// Cached имя active input desktop. Обновляется через pipe notifications от
    /// service's DesktopMonitor (Stage 3). UI под user token не может сам
    /// читать имя Winlogon desktop'а (нет permission). Используется TigerVNC
    /// capture reinit check в MixedRealityPeerConnectionAgent.
    /// </summary>
    // volatile — читается из inject потока (Input.cs) и DXGI callback'а,
    // пишется из pipe read thread'а (OnServiceDesktopChanged). String ref
    // assignment атомарен; volatile гарантирует видимость после write.
    private volatile string _cachedActiveDesktop = "Default";
    private int? _lastSentMouseX;
    private int? _lastSentMouseY;
    private long _lastMouseMoveSentAtMs;
    private readonly SemaphoreSlim _hostVideoSettingsLock = new(1, 1);
    private ConnectionRole _role = ConnectionRole.None;
    private string _currentSessionId = string.Empty;
    private string _currentOwnerSecret = string.Empty; // F-03: ownership proof for close/refresh
    private string _viewerJoinLoginCode = string.Empty; // for viewer reconnect via re-join
    private string _viewerJoinPassCode = string.Empty;
    private volatile bool _callerStartDeferred;
    private int _callerStartedFlag; // Deep-3 fix: atomic via Interlocked
    private volatile bool _hostPeerStateAnnouncedBack;
    private volatile bool _hostVideoStarted;
    private volatile bool _viewerApproved; // NET-05: skip confirmation on reconnect
    private int _clipboardLoopStarted;
    private int _cursorLoopStarted;
    private string _lastObservedClipboardText = string.Empty;
    private string _lastSentClipboardText = string.Empty;
    private string _lastAppliedRemoteClipboardText = string.Empty;
    private DateTime _lastAppliedRemoteClipboardAtUtc = DateTime.MinValue;
    private string _lastSentCursorType = string.Empty;
    private FileTransferService? _fileTransferService;
    private IncomingSaveDirHolder? _incomingSaveDirHolder;
    private OutgoingTargetDirHolder? _outgoingTargetDirHolder;
    /// <summary>Потокобезопасная папка приёма: читается при сохранении входящего файла (любой поток), обновляется из FT.</summary>
    private volatile string? _incomingSaveDirPath;
    private bool _isDataChannelReal;
    private string? _pendingDirListRequestId;
    private string? _pendingCreateFolderRequestId;
    // F-08 (external audit 2026-04-18): было HashSet без sync. Add/Remove из FT
    // callbacks (WebRTC thread pool) + Clear из Connection disposal (UI thread) →
    // race potential (false entries / crash при concurrent modify-during-enumerate).
    // ConcurrentDictionary — lock-free atomic ops для add/remove/contains/clear.
    private readonly ConcurrentDictionary<string, byte> _pendingDeleteRequestIds = new(); // UF-06: track all pending deletes
    private string _fileTransferProgressText = "Передача файлов: idle";
    private bool _fileTransferOnly;
    private string _currentWsUrl = string.Empty;
    private string _currentWsToken = string.Empty;
    // TURN rotating credentials из session response (RFC 7635). Если server выдал
    // их — клиент использует эти вместо static TurnUrl/Username/Password.
    // Null = server в legacy mode, клиент fallback'ится на config.
    private List<SessionClient.TurnServerConfig>? _dynamicTurnServers;
    // TURN status panel timer — Variant C UI (2026-04-25). Tick 1s → recomputes
    // TurnExpiresInText (countdown); fires PropertyChanged for status props когда
    // _dynamicTurnServers меняется или просто истекает.
    private System.Windows.Threading.DispatcherTimer? _turnStatusTimer;
    private string _localIceCandidateType = "unknown";
    private string _remoteIceCandidateType = "unknown";
    private Services.IceUpgradeOptimizer? _iceUpgradeOptimizer;
    private readonly Services.WsReconnectionManager _wsReconnectionManager;
    private string _localIceCandidateIp = "n/a";
    private string _remoteIceCandidateIp = "n/a";
    private string _icePathText = "ICE: unknown";
    private string _iceRecentCandidatesText = string.Empty;
    private readonly Queue<string> _iceRecentCandidates = new();
    private Brush _icePathBrush = Brushes.SlateGray;
    private TaskCompletionSource<bool>? _firstRemoteFrameTcs;
    private bool _uacPromptOnSecureDesktopEnabled = true;
    private Cursor _remoteCursor = Cursors.Arrow;
    private System.Threading.Timer? _sessionCountdownTimer;
    private DateTime _sessionExpiresAtUtc = DateTime.MinValue;
    private string _sessionCountdownText = string.Empty;
    private string _viewerConnectionStatus = string.Empty;
    private Brush _viewerConnectionStatusBrush = Brushes.Transparent;

    public ObservableCollection<string> AvailableDisplayIds { get; } = new();
    public ObservableCollection<string> ViewerAvailableDisplayIds { get; } = new();
    public ObservableCollection<string> ViewerAvailableQualityPresets { get; } = new()
        { "Auto", "Extra Low", "Low", "Medium", "High" };
    public ObservableCollection<string> HostAvailableQualityPresets { get; } = new()
        { "Auto", "Low", "Medium", "High" };
    public ObservableCollection<Contact> Contacts { get; } = new();

    private Contact? _selectedContact;
    public Contact? SelectedContact
    {
        get => _selectedContact;
        set { _selectedContact = value; OnPropertyChanged(nameof(SelectedContact)); }
    }

    private string _newContactName = string.Empty;
    private string _newContactServer = string.Empty;
    private string _newContactLogin = string.Empty;
    private string _newContactPass = string.Empty;
    public string NewContactName { get => _newContactName; set { _newContactName = value ?? string.Empty; OnPropertyChanged(nameof(NewContactName)); } }
    public string NewContactServer { get => _newContactServer; set { _newContactServer = value ?? string.Empty; OnPropertyChanged(nameof(NewContactServer)); } }
    public string NewContactLogin { get => _newContactLogin; set { _newContactLogin = value ?? string.Empty; OnPropertyChanged(nameof(NewContactLogin)); } }
    public string NewContactPass { get => _newContactPass; set { _newContactPass = value ?? string.Empty; OnPropertyChanged(nameof(NewContactPass)); } }

    // UI (MainWindow) can open a separate resizable remote screen window.
    public event Action? RemoteScreenWindowRequested;
    /// <summary>Fires из CleanupConnectionAsync (viewer-side): MainWindow должен спрятать окно
    /// удалённого экрана. Без этого при disconnect/failed reconnect пользователь видит зависший
    /// последний кадр и может ошибочно решить что подключение успешно.</summary>
    public event Action? RemoteScreenWindowShouldHide;
    /// <summary>Fires on host when a viewer connects — UI should flash taskbar and play sound.</summary>
    public event Action? ViewerConnectedNotification;
    /// <summary>Открыть двухпанельное окно передачи файлов. Передаётся FileTransferService, держатель папки приёма и этот MainViewModel (для правой панели = удалённая сторона).</summary>
    public event Action<FileTransfer.FileTransferService, UiApp.Models.IncomingSaveDirHolder, UiApp.Models.OutgoingTargetDirHolder, MainViewModel>? FileTransferWindowRequested;

    /// <summary>Ответ на запрос списка каталога удалённой стороны (для правой панели FT).</summary>
    public event Action<DirListResponsePayload>? RemoteDirListReceived;

    /// <summary>Ошибка при отправке запроса списка (канал не готов и т.д.).</summary>
    public event Action<string>? RequestRemoteDirListFailed;

    /// <summary>Ответ на запрос создания папки на удалённой стороне (для правой панели FT).</summary>
    public event Action<CreateFolderResponsePayload>? CreateFolderResponseReceived;

    /// <summary>Ответ на запрос удаления на удалённой стороне.</summary>
    public event Action<DeleteResponsePayload>? DeleteResponseReceived;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True, если data channel реальный (WebRTC). При false используется mock/loopback — правая панель FT не может показывать удалённый диск.</summary>
    public bool IsRemoteFilePanelAvailable => _isDataChannelReal;

    /// <summary>Лог из окна FT для диагностики (категория FT).</summary>
    public void LogFromFileTransfer(string message) => _logService.Info("FT", message);

    /// <summary>Установить папку приёма входящих файлов из окна FT (левая панель). Вызывается при каждой синхронизации — тогда при сохранении на эту сторону используется эта папка.</summary>
    public void SetIncomingSaveDirFromFileTransfer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (path == _incomingSaveDirPath) return; // dedup — don't log same path repeatedly
        _incomingSaveDirPath = path;
        if (_incomingSaveDirHolder != null)
            _incomingSaveDirHolder.Path = path;
        _logService.Info("FT", "set_incoming_save_dir path=" + path);
    }

    public MainViewModel(SettingsService settingsService, LogService logService, SessionApiClient sessionApiClient, AddressBookService addressBookService)
    {
        _settingsService = settingsService;
        _logService = logService;
        _sessionApiClient = sessionApiClient;
        _addressBookService = addressBookService;
        _recentHistoryService = new RecentHistoryService();
        _settings = _settingsService.Load();
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _windowsInputInjectionService = new WindowsInputInjectionService();
        _wsReconnectionManager = new Services.WsReconnectionManager(
            onLog: msg => _logService.Info("UiApp", msg),
            getRole: () => _role switch
            {
                ConnectionRole.Host => "Host",
                ConnectionRole.Viewer => "Viewer",
                _ => "None"
            },
            getSessionId: () => _currentSessionId);
        foreach (var c in _addressBookService.Load())
        {
            Contacts.Add(c);
        }

        // One-time миграция: в прошлых версиях ad-hoc подключения создавали
        // ephemeral Contact с IsAutoGenerated=true — это засоряло Address Book.
        // Теперь у нас отдельная история (RecentHistoryService), переносим такие
        // контакты туда и удаляем из Contacts. Идемпотентно: если ephemeral нет,
        // ничего не делаем.
        MigrateLegacyEphemeralContacts();

        ApplyDebugLogFilter();

        CreateSessionCommand = new AsyncRelayCommand(CreateSessionAsync);
        JoinSessionCommand = new AsyncRelayCommand(JoinSessionAsync);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
ApplyRemoteVideoSettingsCommand = new AsyncRelayCommand(() => SendViewerVideoSettingsRequestAsync(false));
        QuickReconnectViewerCommand = new AsyncRelayCommand(() => SendViewerVideoSettingsRequestAsync(true));
        RequestHostDisplaysCommand = new AsyncRelayCommand(RequestHostDisplaysAsync);
        ToggleUacSecureDesktopPolicyCommand = new AsyncRelayCommand(ToggleUacSecureDesktopPolicyAsync);
        SendFileCommand = new AsyncRelayCommand(SendFileAsync);
        OpenRemoteDesktopCommand = new AsyncRelayCommand(OpenRemoteDesktopAsync);
        CopySessionCodesCommand = new RelayCommand(CopySessionCodes);
        AddContactCommand = new RelayCommand(AddContact);
        EditContactCommand = new RelayCommand<Contact>(EditContact);
        CancelEditContactCommand = new RelayCommand(CancelEditContact);
        RemoveContactCommand = new RelayCommand<Contact>(RemoveContact);
        ConnectToContactCommand = new AsyncRelayCommand(ConnectToContactAsync);
        ConnectToSpecificContactCommand = new AsyncRelayCommand<Contact>(ConnectToSpecificContactAsync);
        ConnectRecentItemCommand = new AsyncRelayCommand<RecentItem>(ConnectRecentItemAsync);
        SaveRecentAsContactCommand = new RelayCommand<RecentItem>(SaveRecentAsContact);
        LaunchTaskManagerCommand = new AsyncRelayCommand(LaunchTaskManagerAsync);
        RefreshSessionCommand = new AsyncRelayCommand(RefreshSessionAsync);
        TestServerConnectionCommand = new AsyncRelayCommand(TestServerConnectionAsync);
        RefreshUacPolicyState();
        RefreshAvailableDisplayIds();
        RefreshViewerDisplayIdsFallback();
    }

    public void Shutdown()
    {
        // Fire-and-forget wrapper for legacy call sites (App.OnExit etc.).
        // Never block UI thread here - shutdown is awaited from MainWindow.Closing.
        _ = Task.Run(async () =>
        {
            try { await ShutdownAsync(); } catch { /* ignore */ }
        });
    }

    public async Task ShutdownAsync()
    {
        // If machine_id is set, keep the session alive on server so it can be reused after restart.
        if (string.IsNullOrWhiteSpace(_settings.MachineId))
        {
            await TryCloseCurrentSessionAsync().ConfigureAwait(false);
        }
        await CleanupConnectionAsync().ConfigureAwait(false);

        RestoreSecureDesktopPolicy(); // restore UAC policy before exit

        // Unsubscribe event handlers to prevent leaks and post-dispose callbacks.
        if (_servicePipe is not null)
        {
            _servicePipe.ConfigReceived -= ApplyServiceConfig;
        }
        if (_desktopMonitor is not null)
        {
            _desktopMonitor.DesktopChanged -= OnDesktopSwitched;
        }

        try { await (_servicePipe?.NotifyUserExitAsync() ?? Task.CompletedTask); } catch { }
        try { _servicePipe?.Dispose(); _servicePipe = null; } catch { }
        try { _desktopMonitor?.Dispose(); _desktopMonitor = null; } catch { }

        try
        {
            _lifetimeCts.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Best-effort уведомить service через pipe что user'ский exit — service не должен
    /// respawn'ить UI. Вызывается из nuclear ForceClose (MainWindow.xaml.cs) с коротким
    /// timeout'ом чтобы pipe I/O не задерживал TerminateProcess. Отделено от ShutdownAsync
    /// потому что ShutdownAsync делает полный disposе (peer/WS/FT cancel), а nuclear path
    /// их пропускает — нам нужен только pipe signal.
    /// </summary>
    public async Task NotifyServiceUserExitAsync()
    {
        try { await (_servicePipe?.NotifyUserExitAsync() ?? Task.CompletedTask); } catch { }
    }

    /// <summary>
    /// Sync вариант — вызывается из nuclear ForceClose где Task.Wait на UI thread
    /// deadlock'ается с async pipe continuation. Гарантирует доставку user_exit в
    /// service до TerminateProcess (bytes flushed в kernel pipe buffer).
    /// </summary>
    public void NotifyServiceUserExitSync()
    {
        try { _servicePipe?.NotifyUserExitSync(); } catch { }
    }

    public string LoginCode { get; set; } = string.Empty;
    public string PassCode { get; set; } = string.Empty;

    private string _joinLoginCode = string.Empty;
    private string _joinPassCode = string.Empty;

    /// <summary>Логин для вкладки «Подключиться» — не связан с кодами созданной сессии.</summary>
    public string JoinLoginCode
    {
        get => _joinLoginCode;
        set { _joinLoginCode = value ?? string.Empty; OnPropertyChanged(); }
    }
    /// <summary>Пароль для вкладки «Подключиться» — не связан с кодами созданной сессии.</summary>
    public string JoinPassCode
    {
        get => _joinPassCode;
        set { _joinPassCode = value ?? string.Empty; OnPropertyChanged(); }
    }

    public string ServerApiBaseUrl
    {
        get => _settings.ServerApiBaseUrl;
        set { _settings.ServerApiBaseUrl = value; OnPropertyChanged(); }
    }

    public string WebSocketUrl
    {
        get => _settings.WebSocketUrl;
        set { _settings.WebSocketUrl = value; OnPropertyChanged(); }
    }

    public string StunUrl
    {
        get => _settings.StunUrl;
        set { _settings.StunUrl = value; OnPropertyChanged(); }
    }

    public string TurnUrl
    {
        get => _settings.TurnUrl;
        set { _settings.TurnUrl = value; OnPropertyChanged(); }
    }

    public string TurnUsername
    {
        get => _settings.TurnUsername;
        set { _settings.TurnUsername = value; OnPropertyChanged(); }
    }

    public string TurnPassword
    {
        get => _settings.TurnPassword;
        set { _settings.TurnPassword = value; OnPropertyChanged(); }
    }

    // ── TURN Status panel (Variant C UI redesign 2026-04-25) ──────────────
    // Live read-only display of rotating TURN creds. Источник — _dynamicTurnServers
    // (получается из session response). Timer обновляет countdown каждую секунду.

    /// <summary>True если получили rotating creds от сервера (panel показывает active).</summary>
    public bool IsTurnRotationActive =>
        _dynamicTurnServers is { Count: > 0 } list && list[0].Urls.Count > 0;

    /// <summary>True если есть static TurnPassword но нет rotating (manual config).</summary>
    public bool IsTurnStaticConfigured =>
        !IsTurnRotationActive && !string.IsNullOrWhiteSpace(_settings.TurnPassword);

    /// <summary>Локализованный status text для status badge на UI.</summary>
    public string TurnStatusText
    {
        get
        {
            if (IsTurnRotationActive) return Properties.Strings.Settings_TURN_Status_Active;
            if (IsTurnStaticConfigured) return Properties.Strings.Settings_TURN_Status_Static;
            return Properties.Strings.Settings_TURN_Status_None;
        }
    }

    /// <summary>Brush для status indicator dot (Ellipse). Замена emoji'ям т.к. WPF TextBlock
    /// не рендерит color emoji (показывает monochrome bullet). Использует solid colors
    /// (LimeGreen / Goldenrod / DimGray) — как в Contact.PresenceBrush.</summary>
    public System.Windows.Media.Brush TurnStatusBrush
    {
        get
        {
            if (IsTurnRotationActive) return System.Windows.Media.Brushes.LimeGreen;
            if (IsTurnStaticConfigured) return System.Windows.Media.Brushes.Goldenrod;
            return System.Windows.Media.Brushes.DimGray;
        }
    }

    /// <summary>Live URL первого rotating TURN сервера (null если нет).</summary>
    public string TurnLiveUrl
    {
        get
        {
            if (_dynamicTurnServers is { Count: > 0 } list && list[0].Urls.Count > 0)
                return list[0].Urls[0];
            return string.Empty;
        }
    }

    /// <summary>Live username (HMAC-формата "exp_ts:session_id"). Visible read-only — для debug.</summary>
    public string TurnLiveUsername =>
        _dynamicTurnServers is { Count: > 0 } list ? list[0].Username : string.Empty;

    /// <summary>Countdown text "27 мин" / "45 сек". Empty если нет rotating creds.</summary>
    public string TurnExpiresInText
    {
        get
        {
            if (!IsTurnRotationActive) return string.Empty;
            var expiresAt = _dynamicTurnServers![0].ExpiresAtUnix;
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var remaining = expiresAt - nowUnix;
            if (remaining <= 0) return "0 сек"; // refresh imminent / уже expired
            if (remaining < 60)
                return string.Format(Properties.Strings.Settings_TURN_Expires_Soon_Format, remaining);
            return string.Format(Properties.Strings.Settings_TURN_Expires_Format, remaining / 60);
        }
    }

    /// <summary>Запускает 1-сек timer обновления countdown'а. Idempotent.</summary>
    public void StartTurnStatusTimer()
    {
        if (_turnStatusTimer != null) return;
        _turnStatusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _turnStatusTimer.Tick += (_, _) => RefreshTurnStatusBindings();
        _turnStatusTimer.Start();
    }

    /// <summary>Остановить timer (вызывается при закрытии Settings окна для экономии).</summary>
    public void StopTurnStatusTimer()
    {
        _turnStatusTimer?.Stop();
        _turnStatusTimer = null;
    }

    /// <summary>Force refresh всех TURN status binding'ов. Вызывается timer'ом + при
    /// _dynamicTurnServers update (Connection.cs Create/Join/Refresh paths).</summary>
    public void RefreshTurnStatusBindings()
    {
        OnPropertyChanged(nameof(IsTurnRotationActive));
        OnPropertyChanged(nameof(IsTurnStaticConfigured));
        OnPropertyChanged(nameof(TurnStatusText));
        OnPropertyChanged(nameof(TurnStatusBrush));
        OnPropertyChanged(nameof(TurnLiveUrl));
        OnPropertyChanged(nameof(TurnLiveUsername));
        OnPropertyChanged(nameof(TurnExpiresInText));
    }

    /// <summary>UI language code (ru-RU / en-US). Empty = use system default.
    /// Setting this property updates _settings so subsequent SaveSettings() persists
    /// the new value (иначе OnClosing в Settings-окне перезаписывал бы выбор пользователя
    /// stale значением ""). Applied на next app start via App.OnStartup.</summary>
    public string UiLanguage
    {
        get => _settings.UiLanguage;
        set { _settings.UiLanguage = value ?? string.Empty; OnPropertyChanged(); PersistCurrentSettings(nameof(UiLanguage)); }
    }

    /// <summary>Force all ICE traffic через TURN relay. Uncheck "Disable TURN" if this is on.</summary>
    public bool PreferRelay
    {
        get => _settings.PreferRelay;
        set
        {
            if (_settings.PreferRelay == value) return;
            _settings.PreferRelay = value;
            if (value && _settings.PreferLanVpnNoTurn)
            {
                _settings.PreferLanVpnNoTurn = false;
                OnPropertyChanged(nameof(PreferLanVpnNoTurn));
            }
            OnPropertyChanged();
        }
    }

    /// <summary>Disable TURN entirely (STUN/host only). Uncheck "Force relay" if this is on.</summary>
    public bool PreferLanVpnNoTurn
    {
        get => _settings.PreferLanVpnNoTurn;
        set
        {
            if (_settings.PreferLanVpnNoTurn == value) return;
            _settings.PreferLanVpnNoTurn = value;
            if (value && _settings.PreferRelay)
            {
                _settings.PreferRelay = false;
                OnPropertyChanged(nameof(PreferRelay));
            }
            OnPropertyChanged();
        }
    }

    public bool RequireConfirmation
    {
        get => _settings.RequireConfirmation;
        set
        {
            _settings.RequireConfirmation = value;
            OnPropertyChanged();
            // Same fix as AllowUnattended setter — security-critical, persist immediately
            // чтобы restart/crash host'а не reset'ил настройку confirmation prompt.
            try { _settingsService.Save(_settings); }
            catch (Exception ex) { _logService.Warn("Settings", $"require_confirmation_save_failed: {ex.Message}"); }
        }
    }

    public bool AllowUnattended
    {
        get => _settings.AllowUnattended && IsUnattendedPasswordSet;
        set
        {
            // Unattended можно включать ТОЛЬКО когда задан local password.
            var effective = value && IsUnattendedPasswordSet;
            _settings.AllowUnattended = effective;
            OnPropertyChanged();
            // Bug fix 2026-04-26: AllowUnattended — security-critical setting. Раньше
            // он сохранялся только при OnClosing у Settings-окна, что приводило к
            // потере галочки если host крашится / service-spawned UI killed before
            // close → после restart host'а disk содержит false → host отправляет
            // mode=confirmation_only вместо password (viewer видит "Разрешить
            // подключение?" вместо запроса пароля). Теперь persist'им immediately.
            try { _settingsService.Save(_settings); }
            catch (Exception ex) { _logService.Warn("Settings", $"allow_unattended_save_failed: {ex.Message}"); }
        }
    }

    /// <summary>Варианты длительности сессии при «без подтверждения» (для адресной книги). Макс. на сервере — 7 суток.</summary>
    public static IReadOnlyList<SessionDurationOption> UnattendedDurationOptions { get; } = new[]
    {
        new SessionDurationOption { Seconds = 300, Label = "5 минут" },
        new SessionDurationOption { Seconds = 1800, Label = "30 минут" },
        new SessionDurationOption { Seconds = 3600, Label = "1 час" },
        new SessionDurationOption { Seconds = 86400, Label = "24 часа" },
        new SessionDurationOption { Seconds = 604800, Label = "7 дней" }
    };

    /// <summary>Выбранная длительность в секундах (0 = серверный TTL, обычно 5 мин).</summary>
    public int SelectedUnattendedExpiresInSec
    {
        get => _settings.UnattendedExpiresInSec;
        set
        {
            if (_settings.UnattendedExpiresInSec == value) return;
            _settings.UnattendedExpiresInSec = value;
            OnPropertyChanged();
        }
    }

    public string QualityPreset
    {
        get => _settings.QualityPreset;
        set { _settings.QualityPreset = value; OnPropertyChanged(); PersistCurrentSettings(nameof(QualityPreset)); }
    }

    // DisplayMode removed — display selection is now unified into DisplayId (DISPLAY1, DISPLAY2, All)

    public string DisplayId
    {
        get => _settings.DisplayId;
        set { _settings.DisplayId = value; OnPropertyChanged(); PersistCurrentSettings(nameof(DisplayId)); }
    }

    public bool AutoStartOnBoot
    {
        get => _settings.AutoStartOnBoot;
        set
        {
            _settings.AutoStartOnBoot = value;
            OnPropertyChanged();
            // No Registry Run — service manages GUI launch.
        }
    }

    public bool AutoCreateSession
    {
        get => _settings.AutoCreateSession;
        set { _settings.AutoCreateSession = value; OnPropertyChanged(); PersistCurrentSettings(nameof(AutoCreateSession)); }
    }

    public bool AutoUpdateCheckEnabled
    {
        get => _settings.AutoUpdateCheckEnabled;
        set { _settings.AutoUpdateCheckEnabled = value; OnPropertyChanged(); PersistCurrentSettings(nameof(AutoUpdateCheckEnabled)); }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set { _settings.MinimizeToTray = value; OnPropertyChanged(); PersistCurrentSettings(nameof(MinimizeToTray)); }
    }

    public string MachineId => _settings.MachineId;

    /// <summary>Remove stale Registry Run entry (GUI is now launched by service).</summary>
    private static void RemoveAutoStartRegistry()
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "ZConect";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch { }
    }

    /// <summary>Called after construction to auto-create session if enabled.</summary>
    public async Task InitializeOnLoadAsync()
    {

        // Start desktop monitor — detects UAC/lock screen transitions.
        _desktopMonitor = new Services.DesktopMonitor(msg => _logService.Debug("Desktop", msg));
        _desktopMonitor.DesktopChanged += OnDesktopSwitched;
        _desktopMonitor.Start(_lifetimeCts.Token);

        // Wire desktop name callback for DXGI — detects wrong-desktop capture.
        WireDesktopNameCallback();

        // Installer-centric startup: service installation is handled by the InnoSetup installer
        // (ZConect-Setup-*.exe), NOT by the UI itself. UI never spawns/registers the service,
        // never self-restarts under SYSTEM token. If service exists, it will launch this UI in
        // the user session with --from-service; otherwise UI runs in standalone mode.
        var launchedByService = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--from-service", StringComparison.OrdinalIgnoreCase));
        _logService.Info("UiApp", $"init from_service={launchedByService}");

        // Start pipe client — reconnect loop runs in background.
        try
        {
            _servicePipe = new Services.ServicePipeClient(msg => _logService.Debug("Pipe", msg))
            {
                // Если UI запущен через ярлык (не от service), hello message пометит
                // это — service сбросит persistent SuppressAutoSpawnUi флаг (user
                // снова хочет чтобы UI автоматически spawn'ился после reboot).
                LaunchedFromShortcut = !launchedByService,
            };
            _servicePipe.ConfigReceived += ApplyServiceConfig;
            // Stage 3: service шлёт desktop_changed через pipe. UI кэширует значение.
            // Local DesktopMonitor не мог видеть Winlogon после Stage 1 (user token
            // не имеет access на secure desktop), поэтому service — авторитетный
            // источник.
            _servicePipe.DesktopChanged += OnServiceDesktopChanged;
            _ = _servicePipe.ConnectAsync(); // non-blocking, reconnect loop handles retries
        }
        catch (Exception ex)
        {
            _logService.Debug("UiApp", $"service_pipe_init: {ex.Message}");
        }

        // Pre-create WebRTC peer agent while waiting for pipe — overlaps ~0.5s of
        // CPU work with the pipe/network wait. The agent is reused in ConnectWsAsync.
        _preCreatedAgentsTask = Task.Run(() => CreateAndInitPeerAgentAsync(FileTransferOnly));
        _logService.Debug("UiApp", "webrtc_preload_started");

        // Wait briefly for the service to push unattended codes via pipe.
        // - If service is running: we get codes within ~1-2s and continue in service mode.
        // - If service is absent (fresh machine before installer, or user removed it): timeout
        //   falls through to standalone mode. No sc.exe query needed.
        _logService.Info("UiApp", "waiting_for_service_codes");
        SetStatus(Strings.Status_WaitingForService, ConnectionState.Connecting);

        const int maxIterations = 20; // 10 seconds — enough for healthy pipe, short enough for standalone fallback
        bool gotServiceCodes = false;
        for (int i = 0; i < maxIterations; i++)
        {
            await Task.Delay(500);
            if (!string.IsNullOrEmpty(LoginCode) && _servicePipe?.IsConnected == true)
            {
                _logService.Info("UiApp", $"using_service_codes login=****{LoginCode[^4..]}");
                gotServiceCodes = true;
                break;
            }
        }

        if (gotServiceCodes)
        {
            return; // service mode — pipe handler took over
        }

        _logService.Info("UiApp", "service_unavailable_falling_back_to_standalone");

        // Standalone mode: use saved session or create new one.
        if (!string.IsNullOrWhiteSpace(_settings.LastLoginCode)
            && _settings.LastSessionExpiresAtUtcTicks > DateTime.UtcNow.Ticks)
        {
            LoginCode = _settings.LastLoginCode;
            PassCode = _settings.LastPassCode;
            OnPropertyChanged(nameof(LoginCode));
            OnPropertyChanged(nameof(PassCode));
            _logService.Info("UiApp", "restored_saved_session_codes");
        }

        if (_settings.AutoCreateSession && _role == ConnectionRole.None)
        {
            await Task.Delay(300);
            try
            {
                await CreateSessionAsync();
            }
            catch (Exception ex)
            {
                _logService.Warn("UiApp", "auto_create_session_failed_" + ex.Message);
            }
        }
    }

    /// <summary>Called by pipe when service sends config/codes (initial + every reconnect).</summary>
    private void ApplyServiceConfig(Services.PipeConfigMessage cfg)
    {
        if (!cfg.UnattendedEnabled || string.IsNullOrEmpty(cfg.LoginCode)) return;

        // Deduplicate: skip if we already have the same codes (avoids double session
        // creation when both config_sent and config_pushed deliver the same data).
        if (cfg.LoginCode == LoginCode && cfg.PassCode == PassCode) return;

        _uiContext.Post(async _ =>
        {
            LoginCode = cfg.LoginCode;
            PassCode = cfg.PassCode;
            OnPropertyChanged(nameof(LoginCode));
            OnPropertyChanged(nameof(PassCode));
            ServiceStatus = "Запущен (unattended)";
            OnPropertyChanged(nameof(ServiceStatus));

            // Use service's MachineId AND DeviceSecret so CreateSession returns the SAME session/codes.
            // NET-03 server-side check requires both to match for session reuse. Without DeviceSecret,
            // server creates a NEW session with different codes, causing infinite create→crash loop.
            if (!string.IsNullOrEmpty(cfg.MachineId) && cfg.MachineId != _settings.MachineId)
            {
                _logService.Info("UiApp", $"adopting_service_machine_id {cfg.MachineId}");
                _settings.MachineId = cfg.MachineId;
            }
            if (!string.IsNullOrEmpty(cfg.DeviceSecret) && cfg.DeviceSecret != _settings.DeviceSecret)
            {
                _logService.Info("UiApp", "adopting_service_device_secret");
                _settings.DeviceSecret = cfg.DeviceSecret;
            }

            // Adopt ICE server config from service — ensures STUN/TURN credentials are
            // always in sync. Without this, WebRTC agent may use empty/stale credentials
            // from client-settings.json, resulting in no relay candidates and ICE failure.
            if (!string.IsNullOrEmpty(cfg.SignalingUrl)) _settings.ServerApiBaseUrl = cfg.SignalingUrl;
            if (!string.IsNullOrEmpty(cfg.StunUrl)) _settings.StunUrl = cfg.StunUrl;
            if (!string.IsNullOrEmpty(cfg.TurnUrl)) _settings.TurnUrl = cfg.TurnUrl;
            if (!string.IsNullOrEmpty(cfg.TurnUsername)) _settings.TurnUsername = cfg.TurnUsername;
            if (!string.IsNullOrEmpty(cfg.TurnPassword)) _settings.TurnPassword = cfg.TurnPassword;
            if (!string.IsNullOrEmpty(cfg.WebSocketUrl)) _settings.WebSocketUrl = cfg.WebSocketUrl;

            // Service manages GUI launch — no Registry Run needed.
            // Remove any stale Registry Run entry to prevent duplicate instances.
            RemoveAutoStartRegistry();
            _settingsService.Save(_settings);

            _logService.Info("UiApp", $"service_config_applied login=****{cfg.LoginCode[^4..]}");

            // Auto-connect: if service provided WsUrl/WsToken, skip HTTP CreateSession
            // and connect WebSocket directly — saves ~3-4 seconds of network round-trip.
            if (_role == ConnectionRole.None && string.IsNullOrEmpty(_currentSessionId))
            {
                try
                {
                    if (!string.IsNullOrEmpty(cfg.WsToken) && !string.IsNullOrEmpty(cfg.SessionId))
                    {
                        _logService.Info("UiApp", "fast_connect_skipping_http");
                        _role = ConnectionRole.Host;
                        _fileTransferOnly = FileTransferOnly;
                        _currentOwnerSecret = cfg.OwnerSecret;

                        // Persist session info.
                        _settings.LastSessionId = cfg.SessionId;
                        _settings.LastLoginCode = cfg.LoginCode;
                        _settings.LastPassCode = cfg.PassCode;
                        _settings.LastSessionExpiresAtUtcTicks = DateTime.UtcNow.AddHours(24).Ticks;
                        _settingsService.Save(_settings);

                        var wsUrl = !string.IsNullOrEmpty(cfg.WsUrl) ? cfg.WsUrl : WebSocketUrl;
                        var wsOk = await ConnectWsAsync(wsUrl, cfg.WsToken, cfg.SessionId,
                            startAsCaller: false, role: _role, fileTransferOnly: FileTransferOnly);
                        if (wsOk)
                        {
                            _currentOwnerSecret = cfg.OwnerSecret;
                            _currentSessionId = cfg.SessionId; // нужен для RefreshSession ниже
                            StartSessionCountdown(86400);
                            SetStatus(Strings.Status_SessionCreated, ConnectionState.Connecting);
                            _logService.Info("UiApp", "fast_connect_success");

                            // Bug fix 2026-04-26: fast_connect path skips CreateSession, поэтому
                            // _dynamicTurnServers остаётся null до тех пор пока AutoRefresh не
                            // отработает (через ~80% от TTL). User видит static/none на TURN
                            // status panel вместо "rotation active" пока не нажмёт «Сменить пароль».
                            // Fix: fire-and-forget RefreshSession БЕЗ regenerate_pass — просто
                            // подтягиваем fresh turn_servers (server отдаёт их в любом /refresh response).
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var refreshed = await _sessionApiClient.RefreshSessionAsync(
                                        ServerApiBaseUrl, cfg.SessionId, cfg.OwnerSecret, regeneratePass: false);
                                    if (refreshed?.TurnServers is { Count: > 0 } trs)
                                    {
                                        _dynamicTurnServers = trs;
                                        _logService.Info("UiApp", $"turn_creds_received_fast_connect ttl={trs[0].TtlSeconds}s");
                                        _uiContext.Post(_ => RefreshTurnStatusBindings(), null);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logService.Warn("UiApp", $"fast_connect_turn_fetch_failed: {ex.Message}");
                                }
                            });
                        }
                        else
                        {
                            _role = ConnectionRole.None;
                            _logService.Warn("UiApp", "fast_connect_ws_failed_fallback_to_http");
                            await CreateSessionAsync();
                        }
                    }
                    else
                    {
                        _logService.Info("UiApp", "auto_creating_session_with_service_machine_id");
                        await CreateSessionAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logService.Warn("UiApp", $"auto_create_failed: {ex.Message}");
                }
            }
        }, null);
    }

    private string _serviceStatus = "";
    public string ServiceStatus { get => _serviceStatus; set { _serviceStatus = value; OnPropertyChanged(); } }

    /// <summary>Check Windows Service status via sc.exe.</summary>
    public void RefreshServiceStatus()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", "query ZConectService")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            if (output.Contains("RUNNING")) ServiceStatus = "Запущен";
            else if (output.Contains("STOPPED")) ServiceStatus = "Остановлен";
            else ServiceStatus = "Не установлен";
        }
        catch { ServiceStatus = "Не установлен"; }
    }

    /// <summary>Run sc.exe or ZConectService.exe command (requires elevation).</summary>
    public void RunServiceCommand(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", $"{args} ZConectService")
            { UseShellExecute = true, Verb = "runas" };
            System.Diagnostics.Process.Start(psi)?.WaitForExit(10000);
            System.Threading.Thread.Sleep(1000);
            RefreshServiceStatus();
        }
        catch (Exception ex)
        {
            _logService.Warn("UiApp", $"service_command_failed: {ex.Message}");
        }
    }

    // NOTE: AutoInstallAndStartService removed — service installation is now handled
    // by the InnoSetup installer (ZConect-Setup-*.exe). UI no longer self-installs.
    // See installer/ZConect.iss for the install flow.

    public void InstallService()
    {
        // Find ZConectService.exe
        var dir = AppContext.BaseDirectory;
        var exe = Path.Combine(dir, "ZConectService.exe");
        if (!File.Exists(exe))
        {
            // Try sibling
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? dir;
            exe = Path.Combine(parent, "ZConectService", "bin", "Debug", "net8.0-windows", "win-x64", "ZConectService.exe");
        }
        if (!File.Exists(exe)) { StatusText = "ZConectService.exe не найден"; OnPropertyChanged(nameof(StatusText)); return; }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "--install")
            { UseShellExecute = true, Verb = "runas" };
            System.Diagnostics.Process.Start(psi)?.WaitForExit(10000);
            System.Threading.Thread.Sleep(1000);
            RefreshServiceStatus();
        }
        catch (Exception ex)
        {
            _logService.Warn("UiApp", $"service_install_failed: {ex.Message}");
        }
    }

    public void UninstallService()
    {
        var dir = AppContext.BaseDirectory;
        var exe = Path.Combine(dir, "ZConectService.exe");
        if (!File.Exists(exe))
        {
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? dir;
            exe = Path.Combine(parent, "ZConectService", "bin", "Debug", "net8.0-windows", "win-x64", "ZConectService.exe");
        }
        if (!File.Exists(exe)) { RunServiceCommand("stop"); return; }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "--uninstall")
            { UseShellExecute = true, Verb = "runas" };
            System.Diagnostics.Process.Start(psi)?.WaitForExit(10000);
            System.Threading.Thread.Sleep(1000);
            RefreshServiceStatus();
        }
        catch (Exception ex)
        {
            _logService.Warn("UiApp", $"service_uninstall_failed: {ex.Message}");
        }
    }

    public bool DebugLogDataChannelInputEnabled
    {
        get => _settings.DebugLogDataChannelInputEnabled;
        set
        {
            _settings.DebugLogDataChannelInputEnabled = value;
            OnPropertyChanged();
            ApplyDebugLogFilter();
        }
    }

    public bool DebugLogClipboardEnabled
    {
        get => _settings.DebugLogClipboardEnabled;
        set
        {
            _settings.DebugLogClipboardEnabled = value;
            OnPropertyChanged();
            ApplyDebugLogFilter();
        }
    }

    public bool DebugLogSignalingEnabled
    {
        get => _settings.DebugLogSignalingEnabled;
        set
        {
            _settings.DebugLogSignalingEnabled = value;
            OnPropertyChanged();
            ApplyDebugLogFilter();
        }
    }

    public bool DebugLogWebRtcEnabled
    {
        get => _settings.DebugLogWebRtcEnabled;
        set
        {
            _settings.DebugLogWebRtcEnabled = value;
            OnPropertyChanged();
            ApplyDebugLogFilter();
        }
    }

    public bool UacPromptOnSecureDesktopEnabled
    {
        get => _uacPromptOnSecureDesktopEnabled;
        private set
        {
            _uacPromptOnSecureDesktopEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UacPromptOnSecureDesktopStateText));
            OnPropertyChanged(nameof(UacToggleButtonText));
        }
    }

    public string UacPromptOnSecureDesktopStateText => UacPromptOnSecureDesktopEnabled
        ? "Сейчас: защищенный рабочий стол UAC включен (запросы могут не отображаться удаленно)"
        : "Сейчас: защищенный рабочий стол UAC выключен (запросы видны удаленно)";

    public string UacToggleButtonText => UacPromptOnSecureDesktopEnabled
        ? "Разрешить показ UAC в удаленной сессии"
        : "Вернуть защищенный рабочий стол UAC (безопаснее)";

    public string StatusText { get; private set; } = "Ready";

    // ── Auto-update notification (visual-only, не auto-install) ────────────
    private bool _isUpdateAvailable;
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set
        {
            if (_isUpdateAvailable == value) return;
            _isUpdateAvailable = value;
            OnPropertyChanged();
        }
    }

    private string _latestVersionText = string.Empty;
    public string LatestVersionText
    {
        get => _latestVersionText;
        set { _latestVersionText = value; OnPropertyChanged(); }
    }

    /// <summary>URL из UpdateInfo.DownloadUrl — куда ведёт click на update badge.</summary>
    public string UpdateDownloadUrl { get; set; } = string.Empty;

    /// <summary>Release notes для tooltip над badge'ом.</summary>
    public string UpdateReleaseNotes { get; set; } = string.Empty;

    /// <summary>SHA256 checksum installer'а (64 hex). Показывается коротко в tooltip.</summary>
    private string _updateSha256 = string.Empty;
    public string UpdateSha256
    {
        get => _updateSha256;
        set
        {
            _updateSha256 = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateSha256Short));
            OnPropertyChanged(nameof(HasUpdateSha256));
        }
    }
    public string UpdateSha256Short => string.IsNullOrEmpty(_updateSha256)
        ? string.Empty
        : $"SHA256: {_updateSha256[..Math.Min(16, _updateSha256.Length)]}…";
    public bool HasUpdateSha256 => !string.IsNullOrEmpty(_updateSha256);

    private ConnectionState _connectionState = ConnectionState.Idle;
    public ConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            _connectionState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDotBrush));
            OnPropertyChanged(nameof(IsConnecting));
        }
    }

    public bool IsConnecting => _connectionState == ConnectionState.Connecting;

    /// <summary>4-step progress для viewer'а при подключении: 1=Сигналинг,
    /// 2=ICE negotiation, 3=DataChannel open, 4=Ready (видео идёт).
    /// 0 = idle / not connecting. XAML использует DataTriggers по этому значению
    /// чтобы заполнять pill-indicators (●/○).</summary>
    private int _connectionStep;
    public int ConnectionStep
    {
        get => _connectionStep;
        private set
        {
            if (_connectionStep == value) return;
            _connectionStep = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectionStepVisible));
            OnPropertyChanged(nameof(Step1Filled));
            OnPropertyChanged(nameof(Step2Filled));
            OnPropertyChanged(nameof(Step3Filled));
            OnPropertyChanged(nameof(Step4Filled));
        }
    }

    public bool ConnectionStepVisible => IsJoiningAsViewer && _connectionStep is > 0 and < 4;
    public bool Step1Filled => _connectionStep >= 1;
    public bool Step2Filled => _connectionStep >= 2;
    public bool Step3Filled => _connectionStep >= 3;
    public bool Step4Filled => _connectionStep >= 4;

    /// <summary>
    /// True только пока VIEWER активно пытается подключиться (между кликом на
    /// "Подключиться" и завершением join flow). Не путать с IsConnecting —
    /// host после Create Session остаётся в ConnectionState.Connecting
    /// пока viewer не придёт, но его кнопка "Подключиться" анимироваться
    /// не должна. Используется для ProgressBar на Connect button в viewer tab'е.
    /// </summary>
    private bool _isJoiningAsViewer;
    public bool IsJoiningAsViewer
    {
        get => _isJoiningAsViewer;
        private set
        {
            if (_isJoiningAsViewer == value) return;
            _isJoiningAsViewer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectionStepVisible));
        }
    }

    public Brush StatusDotBrush => _connectionState switch
    {
        ConnectionState.Connecting => BrushAmber, // янтарный
        ConnectionState.Connected  => BrushGreen, // зелёный
        ConnectionState.Error      => BrushRed,   // красный
        _                          => BrushGray   // серый
    };

    /// <summary>Задаёт StatusText и ConnectionState одним вызовом; уведомляет UI.</summary>
    internal void SetStatus(string text, ConnectionState state)
    {
        StatusText = text;
        ConnectionState = state;
        OnPropertyChanged(nameof(StatusText));
    }
    public string IcePathText
    {
        get => _icePathText;
        private set
        {
            _icePathText = value;
            OnPropertyChanged();
        }
    }
    public Brush IcePathBrush
    {
        get => _icePathBrush;
        private set
        {
            // Defensive Freeze: caller может создать Brush на background thread.
            // Frozen brush — thread-safe для DependencyProperty binding attach.
            value ??= Brushes.Transparent;
            if (value.CanFreeze && !value.IsFrozen) value.Freeze();
            _icePathBrush = value;
            OnPropertyChanged();
        }
    }
    public string IceRecentCandidatesText
    {
        get => _iceRecentCandidatesText;
        private set
        {
            _iceRecentCandidatesText = value;
            OnPropertyChanged();
        }
    }
    public string FileTransferProgressText
    {
        get => _fileTransferProgressText;
        private set
        {
            _fileTransferProgressText = value;
            OnPropertyChanged();
        }
    }
    public Cursor RemoteCursor
    {
        get => _remoteCursor;
        private set
        {
            _remoteCursor = value;
            OnPropertyChanged();
        }
    }
    public ImageSource? RemoteFrameImage { get; private set; }
    public string ViewerRequestedQualityPreset { get; set; } = "Auto";

    // ViewerRequestedDisplayMode removed — viewer now uses ViewerRequestedDisplayId directly

    private string _viewerRequestedDisplayId = string.Empty;
    public string ViewerRequestedDisplayId
    {
        get => _viewerRequestedDisplayId;
        set { _viewerRequestedDisplayId = value ?? string.Empty; OnPropertyChanged(nameof(ViewerRequestedDisplayId)); }
    }

    /// <summary>Обратный отсчёт оставшегося времени сессии, например «Сессия: 54:23».</summary>
    public string SessionCountdownText
    {
        get => _sessionCountdownText;
        private set
        {
            if (_sessionCountdownText == value) return;
            _sessionCountdownText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Текст статуса подключённого зрителя (пустой, если нет подключения).</summary>
    public string ViewerConnectionStatus
    {
        get => _viewerConnectionStatus;
        private set
        {
            if (_viewerConnectionStatus == value) return;
            _viewerConnectionStatus = value;
            OnPropertyChanged();
        }
    }

    public Brush ViewerConnectionStatusBrush
    {
        get => _viewerConnectionStatusBrush;
        private set
        {
            // Defensive Freeze (см. IcePathBrush).
            value ??= Brushes.Transparent;
            if (value.CanFreeze && !value.IsFrozen) value.Freeze();
            _viewerConnectionStatusBrush = value;
            OnPropertyChanged();
        }
    }

    public ICommand CreateSessionCommand { get; }
    public ICommand JoinSessionCommand { get; }
    public ICommand SaveSettingsCommand { get; }
public ICommand ApplyRemoteVideoSettingsCommand { get; }
    public ICommand QuickReconnectViewerCommand { get; }
    public ICommand RequestHostDisplaysCommand { get; }
    public ICommand ToggleUacSecureDesktopPolicyCommand { get; }
    public ICommand SendFileCommand { get; }
    public ICommand OpenRemoteDesktopCommand { get; }
    public ICommand CopySessionCodesCommand { get; }
    public ICommand AddContactCommand { get; }
    public ICommand EditContactCommand { get; }
    public ICommand CancelEditContactCommand { get; }
    public ICommand RemoveContactCommand { get; }
    public ICommand ConnectToContactCommand { get; }
    /// <summary>Подключение к конкретному контакту (без SelectedContact). CommandParameter = Contact.</summary>
    public ICommand ConnectToSpecificContactCommand { get; }
    /// <summary>Подключение из Recent row. CommandParameter = RecentItem.
    /// Unified: для IsSaved=true делегирует в ConnectToSpecificContactAsync (with Contact),
    /// для IsSaved=false — ad-hoc connect с login/pass из history записи.</summary>
    public ICommand ConnectRecentItemCommand { get; }
    /// <summary>«Сохранить в адресную книгу» из Recent card (visible только для history).
    /// CommandParameter = RecentItem. Открывает PromptDialog, создаёт Contact, удаляет
    /// из history.</summary>
    public ICommand SaveRecentAsContactCommand { get; }
    public ICommand LaunchTaskManagerCommand { get; }
    public ICommand RefreshSessionCommand { get; }
    public ICommand TestServerConnectionCommand { get; }

    private Contact? _editingContact;
    /// <summary>Режим редактирования: выбранный контакт, данные в форме справа.</summary>
    public Contact? EditingContact
    {
        get => _editingContact;
        private set
        {
            if (_editingContact == value) return;
            _editingContact = value;
            OnPropertyChanged(nameof(EditingContact));
            OnPropertyChanged(nameof(ContactFormTitle));
            OnPropertyChanged(nameof(ContactFormButtonText));
            OnPropertyChanged(nameof(IsEditingContact));
        }
    }

    public string ContactFormTitle => EditingContact is null ? Strings.AddContactTitle : Strings.EditContactTitle;
    public string ContactFormButtonText => EditingContact is null ? Strings.AddButton : Strings.Button_Save;
    public bool IsEditingContact => EditingContact is not null;

    /// <summary>Режим «только передача файлов»: при создании/подключении видео не добавляется в SDP; по кнопке «Удалённый рабочий стол» — renegotiation.</summary>
    public bool FileTransferOnly
    {
        get => _fileTransferOnly;
        set
        {
            if (_fileTransferOnly == value) return;
            _fileTransferOnly = value;
            OnPropertyChanged(nameof(FileTransferOnly));
        }
    }

    /// <summary>True, если подключены как viewer — можно открыть окно удалённого стола (или запустить renegotiation в режиме «только файлы»).</summary>
    public bool IsViewerAndConnected => _role == ConnectionRole.Viewer && _signalingClient is not null;

    /// <summary>Видимость кнопки «Удалённый рабочий стол» — только для подключённого зрителя.</summary>
    public System.Windows.Visibility RemoteDesktopButtonVisibility => IsViewerAndConnected ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>
    /// Helper для setter'ов которые меняют security/UX-critical setting и должны
    /// persist immediately. Если service-spawned UI killed (или crash) до OnClosing
    /// у Settings-окна, без immediate save value теряется. Audit 2026-04-26 нашёл
    /// 6 такого паттерна setter'ов (QualityPreset, DisplayId, AutoCreate,
    /// AutoUpdate, MinimizeToTray, UiLanguage); этот helper - DRY common path.
    /// </summary>
    private void PersistCurrentSettings(string propertyName)
    {
        try { _settingsService.Save(_settings); }
        catch (Exception ex) { _logService.Warn("Settings", $"persist_failed property={propertyName} err={ex.Message}"); }
    }

    /// <summary>
    /// Помечает onboarding как пройденный. Вызывается из MainWindow.CompleteOnboarding().
    /// КРИТИЧНО обновлять in-memory _settings, а не только disk: иначе следующий
    /// SaveSettings() (из OnClosing Settings-окна, language-toggle, video setting и т.д.)
    /// перезапишет true → false из stale памяти. Bug 2026-04-26: onboarding запускался
    /// после каждого закрытия Settings-окна.
    /// </summary>
    public void MarkOnboardingCompleted(bool privacyAcked = false)
    {
        _settings.OnboardingCompleted = true;
        if (privacyAcked && _settings.PrivacyPolicyAckedAtUtc is null)
        {
            // Audit trail: только при первом подтверждении пишем timestamp.
            // Перезапись более ранней даты не нужна — раз ознакомился, ознакомился.
            _settings.PrivacyPolicyAckedAtUtc = DateTime.UtcNow;
        }
        try { _settingsService.Save(_settings); }
        catch (Exception ex) { _logService.Warn("Settings", $"onboarding_save_failed: {ex.Message}"); }
    }

    public void SaveSettings()
    {
        ApplyDebugLogFilter();
        RefreshAvailableDisplayIds();
        _settingsService.Save(_settings);
        _logService.Info("UiApp", $"settings_saved path={_settingsService.SettingsPath}");

        // Service-owned настройки (TURN credentials, STUN url) — персистит service,
        // не client. Без push'а service-config.json остаётся со старыми значениями,
        // изменённый через UI TurnPassword игнорируется на next WebRTC connect.
        _ = _servicePipe?.UpdateServiceConfigAsync(
            turnUrl: _settings.TurnUrl,
            turnUsername: _settings.TurnUsername,
            turnPassword: _settings.TurnPassword,
            stunUrl: _settings.StunUrl);
    }

    // ── Server connection test ───────────────────────────────────────────

    private string _serverTestResult = string.Empty;
    private Brush _serverTestBrush = Brushes.Transparent;

    public string ServerTestResult
    {
        get => _serverTestResult;
        private set { _serverTestResult = value; OnPropertyChanged(); }
    }

    public Brush ServerTestBrush
    {
        get => _serverTestBrush;
        private set
        {
            // Defensive Freeze (см. IcePathBrush).
            value ??= Brushes.Transparent;
            if (value.CanFreeze && !value.IsFrozen) value.Freeze();
            _serverTestBrush = value;
            OnPropertyChanged();
        }
    }

    private async Task TestServerConnectionAsync()
    {
        ServerTestResult = Strings.Status_Checking;
        ServerTestBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var http = new System.Net.Http.HttpClient();
            var resp = await http.GetAsync(ServerApiBaseUrl.TrimEnd('/') + "/healthz", cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                ServerTestResult = "API: OK";
                ServerTestBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            }
            else
            {
                ServerTestResult = $"API: {(int)resp.StatusCode}";
                ServerTestBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
        }
        catch (Exception ex)
        {
            ServerTestResult = "Ошибка: " + (ex.InnerException?.Message ?? ex.Message);
            ServerTestBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
    }

    // ── Log file management ──────────��───────────────────────────────────

    // LogService writes to C:\ProgramData\ZConect\logs\ui.log — keep these helpers in sync.
    private static string UiLogDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ZConect", "logs");

    public string LogFileSizeText
    {
        get
        {
            try
            {
                var dir = UiLogDir;
                if (!System.IO.Directory.Exists(dir)) return "Логов нет";
                long total = 0;
                int count = 0;
                foreach (var f in System.IO.Directory.GetFiles(dir, "ui*.log"))
                {
                    total += new System.IO.FileInfo(f).Length;
                    count++;
                }
                if (count == 0) return "Логов нет";
                var mb = total / (1024.0 * 1024.0);
                return mb >= 1 ? $"{mb:F1} MB ({count} файл.)" : $"{total / 1024.0:F0} KB ({count} файл.)";
            }
            catch { return "—"; }
        }
    }

    public void OpenLogFile()
    {
        try
        {
            var path = System.IO.Path.Combine(UiLogDir, "ui.log");
            if (!System.IO.File.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    public void ClearLogFiles()
    {
        try
        {
            var dir = UiLogDir;
            if (!System.IO.Directory.Exists(dir)) return;
            foreach (var f in System.IO.Directory.GetFiles(dir, "ui*.log"))
            {
                try { System.IO.File.Delete(f); } catch { /* ignore */ }
            }
            OnPropertyChanged(nameof(LogFileSizeText));
        }
        catch { /* ignore */ }
    }

    // ── Language stub ────────────────────────────────────────────────────

    public static IReadOnlyList<string> AvailableLanguages { get; } = new[] { "Русский", "English" };

    private string _selectedLanguage = "Русский";
    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (_selectedLanguage == value) return;
            _selectedLanguage = value;
            OnPropertyChanged();
        }
    }

    private void CopySessionCodes()
    {
        try
        {
            var login = LoginCode?.Trim() ?? string.Empty;
            var pass = PassCode?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(pass))
            {
                StatusText = "Сначала создайте сессию";
                OnPropertyChanged(nameof(StatusText));
                return;
            }

            Clipboard.SetText($"Логин: {login}{Environment.NewLine}Пароль: {pass}");
            StatusText = Strings.Status_CopiedLoginPassword;
            OnPropertyChanged(nameof(StatusText));
        }
        catch (Exception ex)
        {
            StatusText = "Не удалось скопировать логин/пароль";
            OnPropertyChanged(nameof(StatusText));
            _logService.Warn("UiApp", "copy_session_codes_failed_" + ex.Message);
        }
    }

    // ── Session countdown timer + auto-refresh ────────────────────────────

    private void StartSessionCountdown(int expiresInSec)
    {
        StopSessionCountdown();
        _sessionExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresInSec);
        _sessionCountdownTimer = new System.Threading.Timer(OnSessionCountdownTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void StopSessionCountdown()
    {
        _sessionCountdownTimer?.Dispose();
        _sessionCountdownTimer = null;
        _sessionExpiresAtUtc = DateTime.MinValue;
        _uiContext.Post(_ => SessionCountdownText = string.Empty, null);
    }

    private void OnSessionCountdownTick(object? state)
    {
        // Guard: timer may fire after StopSessionCountdown/ShutdownAsync disposed resources.
        try
        {
            if (_lifetimeCts.IsCancellationRequested || _sessionCountdownTimer is null) return;
        }
        catch (ObjectDisposedException) { return; }

        var remaining = _sessionExpiresAtUtc - DateTime.UtcNow;
        if (remaining.TotalSeconds <= 0)
        {
            try
            {
                _uiContext.Post(_ =>
                {
                    SessionCountdownText = Strings.Status_SessionExpired;
                    SetStatus(Strings.Status_SessionExpired, ConnectionState.Error);
                }, null);
            }
            catch { /* UI context may be gone during shutdown */ }
            StopSessionCountdown();
            return;
        }

        var text = remaining.TotalHours >= 1
            ? string.Format(Strings.Session_Countdown_HoursFormat, (int)remaining.TotalHours, remaining.Minutes, remaining.Seconds)
            : string.Format(Strings.Session_Countdown_MinSecFormat, remaining.Minutes, remaining.Seconds);

        try { _uiContext.Post(_ => SessionCountdownText = text, null); }
        catch { /* UI context may be gone during shutdown */ }

        // Auto-refresh password when 20% of TTL remaining (min 60 sec before expiry).
        var settingsTtl = _settings.UnattendedExpiresInSec > 0 ? _settings.UnattendedExpiresInSec : 300;
        var threshold = Math.Max(settingsTtl * 0.2, 60);
        if (_role == ConnectionRole.Host
            && !string.IsNullOrEmpty(_currentSessionId)
            && remaining.TotalSeconds <= threshold)
        {
            _ = Task.Run(async () =>
            {
                try { await AutoRefreshSessionAsync(); }
                catch { /* ignore */ }
            });
        }
    }

    private int _autoRefreshInProgress;

    private async Task AutoRefreshSessionAsync()
    {
        if (Interlocked.CompareExchange(ref _autoRefreshInProgress, 1, 0) != 0)
            return; // already refreshing

        try
        {
            var requestedTtl = _settings.UnattendedExpiresInSec > 0 ? _settings.UnattendedExpiresInSec : (int?)null;
            var response = await _sessionApiClient.RefreshSessionAsync(ServerApiBaseUrl, _currentSessionId, _currentOwnerSecret, requestedTtl, regeneratePass: true);
            if (response is null) return;

            _settings.LastPassCode = response.PassCode;
            _settings.LastSessionExpiresAtUtcTicks = DateTime.UtcNow.AddSeconds(response.ExpiresInSec).Ticks;
            _settingsService.Save(_settings);

            _sessionExpiresAtUtc = DateTime.UtcNow.AddSeconds(response.ExpiresInSec);

            // Rotating TURN: refresh response contains fresh creds. Apply them so
            // next WebRTC session/reconnect uses new creds. Без этого _dynamicTurnServers
            // остаётся null когда PC запускается через fast_connect (без CreateSession)
            // и только AutoRefresh отрабатывает — ICE config remains without TURN.
            if (response.TurnServers is not null && response.TurnServers.Count > 0)
            {
                _dynamicTurnServers = response.TurnServers;
                _logService.Info("UiApp", $"turn_creds_refreshed_auto ttl={response.TurnServers[0].TtlSeconds}s");
                _uiContext.Post(_ => RefreshTurnStatusBindings(), null);
            }

            _uiContext.Post(_ =>
            {
                PassCode = response.PassCode;
                OnPropertyChanged(nameof(PassCode));
            }, null);

            _logService.Info("UiApp", "auto_refresh_session_success");
        }
        finally
        {
            Interlocked.Exchange(ref _autoRefreshInProgress, 0);
        }
    }

    // ─────────────────────────────────────────────────────────────────────

    private void ApplyDebugLogFilter()
    {
        _logService.SetDebugFilter(new LogService.DebugLogFilterOptions
        {
            DataChannelInputEnabled = _settings.DebugLogDataChannelInputEnabled,
            ClipboardEnabled = _settings.DebugLogClipboardEnabled,
            SignalingEnabled = _settings.DebugLogSignalingEnabled,
            WebRtcEnabled = _settings.DebugLogWebRtcEnabled
        });
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private T _uiContextInvoke<T>(Func<T> action)
    {
        T result = default!;
        _uiContext.Send(_ =>
        {
            result = action();
        }, null);
        return result;
    }

    private Task<T> _uiContextInvokeAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(_ =>
        {
            try
            {
                tcs.TrySetResult(action());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);
        return tcs.Task;
    }
}
