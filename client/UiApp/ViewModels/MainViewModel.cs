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

    private readonly SettingsService _settingsService;
    private readonly LogService _logService;
    private readonly SessionApiClient _sessionApiClient;
    private readonly AddressBookService _addressBookService;
    private WebSocketSignalingClient? _signalingClient;
    private SignalingCoordinator? _signalingCoordinator;
    private DataChannelCoordinator? _dataChannelCoordinator;
    private MixedRealityPeerConnectionAgent? _realPeerAgent;
    private ClientSettings _settings;
    private readonly SynchronizationContext _uiContext;
    private readonly WindowsInputInjectionService _windowsInputInjectionService;
    private CancellationTokenSource _lifetimeCts = new();
    private WriteableBitmap? _remoteFrameBitmap;
    private ScreenMetaPayload? _remoteScreenMeta;
    private ScreenMetaPayload? _localScreenMeta;
    private int? _lastSentMouseX;
    private int? _lastSentMouseY;
    private long _lastMouseMoveSentAtMs;
    private readonly SemaphoreSlim _hostVideoSettingsLock = new(1, 1);
    private ConnectionRole _role = ConnectionRole.None;
    private string _currentSessionId = string.Empty;
    private volatile bool _callerStartDeferred;
    private volatile bool _callerStarted;
    private volatile bool _hostPeerStateAnnouncedBack;
    private volatile bool _hostVideoStarted;
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
    private string? _pendingDeleteRequestId;
    private string _fileTransferProgressText = "Передача файлов: idle";
    private bool _fileTransferOnly;
    private string _currentWsUrl = string.Empty;
    private string _currentWsToken = string.Empty;
    private string _localIceCandidateType = "unknown";
    private string _remoteIceCandidateType = "unknown";
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
        _settings = _settingsService.Load();
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _windowsInputInjectionService = new WindowsInputInjectionService();
        foreach (var c in _addressBookService.Load())
        {
            Contacts.Add(c);
        }
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
        SendCtrlAltDelCommand = new AsyncRelayCommand(SendCtrlAltDelAsync);
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

        try
        {
            _lifetimeCts.Dispose();
        }
        catch
        {
            // ignore
        }
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

    public bool RequireConfirmation
    {
        get => _settings.RequireConfirmation;
        set { _settings.RequireConfirmation = value; OnPropertyChanged(); }
    }

    public bool AllowUnattended
    {
        get => _settings.AllowUnattended;
        set { _settings.AllowUnattended = value; OnPropertyChanged(); }
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
        set { _settings.QualityPreset = value; OnPropertyChanged(); }
    }

    // DisplayMode removed — display selection is now unified into DisplayId (DISPLAY1, DISPLAY2, All)

    public string DisplayId
    {
        get => _settings.DisplayId;
        set { _settings.DisplayId = value; OnPropertyChanged(); }
    }

    public bool AutoStartOnBoot
    {
        get => _settings.AutoStartOnBoot;
        set
        {
            _settings.AutoStartOnBoot = value;
            OnPropertyChanged();
            ApplyAutoStartRegistry();
        }
    }

    public bool AutoCreateSession
    {
        get => _settings.AutoCreateSession;
        set { _settings.AutoCreateSession = value; OnPropertyChanged(); }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set { _settings.MinimizeToTray = value; OnPropertyChanged(); }
    }

    public string MachineId => _settings.MachineId;

    /// <summary>Apply or remove auto-start registry key.</summary>
    private void ApplyAutoStartRegistry()
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "ZConect";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key is null) return;

            if (_settings.AutoStartOnBoot)
            {
                var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
                if (!string.IsNullOrWhiteSpace(exePath))
                    key.SetValue(valueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Registry access may fail — ignore silently
        }
    }

    /// <summary>Called after construction to auto-create session if enabled.</summary>
    public async Task InitializeOnLoadAsync()
    {
        // If there is a saved session that hasn't expired, show its codes immediately.
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
            await Task.Delay(300); // let window render
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

    public Brush StatusDotBrush => _connectionState switch
    {
        ConnectionState.Connecting => new SolidColorBrush(Color.FromRgb(245, 158, 11)),  // янтарный
        ConnectionState.Connected  => new SolidColorBrush(Color.FromRgb(34,  197, 94)),  // зелёный
        ConnectionState.Error      => new SolidColorBrush(Color.FromRgb(239, 68,  68)),  // красный
        _                          => new SolidColorBrush(Color.FromRgb(156, 163, 175))  // серый
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
    public ICommand SendCtrlAltDelCommand { get; }
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

    public string ContactFormTitle => EditingContact is null ? "Добавить контакт" : "Изменить контакт";
    public string ContactFormButtonText => EditingContact is null ? "Добавить" : "Сохранить";
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

    public void SaveSettings()
    {
        ApplyDebugLogFilter();
        RefreshAvailableDisplayIds();
        _settingsService.Save(_settings);
        _logService.Info("UiApp", "settings_saved");
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
        private set { _serverTestBrush = value; OnPropertyChanged(); }
    }

    private async Task TestServerConnectionAsync()
    {
        ServerTestResult = "Проверка...";
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

    public string LogFileSizeText
    {
        get
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                long total = 0;
                int count = 0;
                foreach (var f in System.IO.Directory.GetFiles(baseDir, "logs*.log"))
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
            var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs.log");
            if (!System.IO.File.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    public void ClearLogFiles()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var f in System.IO.Directory.GetFiles(baseDir, "logs*.log"))
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
            StatusText = "Логин и пароль скопированы";
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
        var remaining = _sessionExpiresAtUtc - DateTime.UtcNow;
        if (remaining.TotalSeconds <= 0)
        {
            _uiContext.Post(_ =>
            {
                SessionCountdownText = "Сессия истекла";
                SetStatus("Сессия истекла", ConnectionState.Error);
            }, null);
            StopSessionCountdown();
            return;
        }

        var text = remaining.TotalHours >= 1
            ? $"Сессия: {(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
            : $"Сессия: {remaining.Minutes:D2}:{remaining.Seconds:D2}";

        _uiContext.Post(_ => SessionCountdownText = text, null);

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
            var response = await _sessionApiClient.RefreshSessionAsync(ServerApiBaseUrl, _currentSessionId, requestedTtl);
            if (response is null) return;

            _settings.LastPassCode = response.PassCode;
            _settings.LastSessionExpiresAtUtcTicks = DateTime.UtcNow.AddSeconds(response.ExpiresInSec).Ticks;
            _settingsService.Save(_settings);

            _sessionExpiresAtUtc = DateTime.UtcNow.AddSeconds(response.ExpiresInSec);

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
