using System.ComponentModel;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using Microsoft.Win32;
using QualityController;
using ScreenCapture;
using SessionClient;
using UiApp.Models;
using UiApp.Services;
using WebRtcTransport;

namespace UiApp.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const int ClipboardMaxBytes = 256 * 1024;
    private const int ClipboardPollMs = 250;
    private const int FileChunkSizeBytes = 16 * 1024;
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
    private readonly ConcurrentDictionary<string, IncomingFileTransferState> _incomingFileTransfers = new();
    private string _fileTransferProgressText = "Передача файлов: idle";
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
    public ObservableCollection<string> AvailableDisplayIds { get; } = new();
    public ObservableCollection<string> ViewerAvailableDisplayIds { get; } = new();

    // UI (MainWindow) can open a separate resizable remote screen window.
    public event Action? RemoteScreenWindowRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(SettingsService settingsService, LogService logService, SessionApiClient sessionApiClient)
    {
        _settingsService = settingsService;
        _logService = logService;
        _sessionApiClient = sessionApiClient;
        _settings = _settingsService.Load();
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _windowsInputInjectionService = new WindowsInputInjectionService();
        ApplyDebugLogFilter();

        CreateSessionCommand = new AsyncRelayCommand(CreateSessionAsync);
        JoinSessionCommand = new AsyncRelayCommand(JoinSessionAsync);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        RunFfmpegProbeCommand = new AsyncRelayCommand(RunFfmpegProbeAsync);
        ApplyRemoteVideoSettingsCommand = new AsyncRelayCommand(() => SendViewerVideoSettingsRequestAsync(false));
        QuickReconnectViewerCommand = new AsyncRelayCommand(() => SendViewerVideoSettingsRequestAsync(true));
        ToggleUacSecureDesktopPolicyCommand = new AsyncRelayCommand(ToggleUacSecureDesktopPolicyAsync);
        SendFileCommand = new AsyncRelayCommand(SendFileAsync);
        CopySessionCodesCommand = new RelayCommand(CopySessionCodes);
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
        await TryCloseCurrentSessionAsync().ConfigureAwait(false);
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

    public string FfmpegPath
    {
        get => _settings.FfmpegPath;
        set { _settings.FfmpegPath = value; OnPropertyChanged(); }
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

    public bool PreferRelay
    {
        get => _settings.PreferRelay;
        set { _settings.PreferRelay = value; OnPropertyChanged(); }
    }

    public bool PreferLanVpnNoTurn
    {
        get => _settings.PreferLanVpnNoTurn;
        set { _settings.PreferLanVpnNoTurn = value; OnPropertyChanged(); }
    }

    public bool AutoIceByPriority
    {
        get => _settings.AutoIceByPriority;
        set { _settings.AutoIceByPriority = value; OnPropertyChanged(); }
    }

    public string QualityPreset
    {
        get => _settings.QualityPreset;
        set { _settings.QualityPreset = value; OnPropertyChanged(); }
    }

    public string DisplayMode
    {
        get => _settings.DisplayMode;
        set { _settings.DisplayMode = value; OnPropertyChanged(); }
    }

    public string DisplayId
    {
        get => _settings.DisplayId;
        set { _settings.DisplayId = value; OnPropertyChanged(); }
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
    public string ViewerRequestedDisplayMode { get; set; } = "Current";
    public string ViewerRequestedDisplayId { get; set; } = string.Empty;

    public ICommand CreateSessionCommand { get; }
    public ICommand JoinSessionCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand RunFfmpegProbeCommand { get; }
    public ICommand ApplyRemoteVideoSettingsCommand { get; }
    public ICommand QuickReconnectViewerCommand { get; }
    public ICommand ToggleUacSecureDesktopPolicyCommand { get; }
    public ICommand SendFileCommand { get; }
    public ICommand CopySessionCodesCommand { get; }

    private async Task CreateSessionAsync()
    {
        // If host already created and waiting/connected, do not start a new session on top.
        if (_role == ConnectionRole.Host && _signalingClient is not null)
        {
            StatusText = "Сессия уже создана";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        StatusText = "Создание сессии...";
        OnPropertyChanged(nameof(StatusText));
        _logService.Info("UiApp", "create_session_clicked");

        var response = await _sessionApiClient.CreateSessionAsync(ServerApiBaseUrl, AllowUnattended);
        if (response is null)
        {
            StatusText = "Ошибка создания сессии";
            OnPropertyChanged(nameof(StatusText));
            _logService.Warn("UiApp", "create_session_failed");
            return;
        }

        var loginCode = response.LoginCode;
        var passCode = response.PassCode;
        var expiresInSec = response.ExpiresInSec;
        var wsUrl = response.WsUrl;
        var wsToken = response.WsToken;
        var sessionId = response.SessionId;

        _uiContext.Post(_ =>
        {
            LoginCode = loginCode;
            PassCode = passCode;
            OnPropertyChanged(nameof(LoginCode));
            OnPropertyChanged(nameof(PassCode));
        }, null);

        // Host mode: пользователь просит помощи -> шарим экран + принимаем управление.
        _role = ConnectionRole.Host;
        await ConnectWsAsync(wsUrl, wsToken, sessionId, startAsCaller: false, role: _role);

        _uiContext.Post(_ =>
        {
            StatusText = $"Сессия создана, ожидаем подключение ({expiresInSec} сек)";
            OnPropertyChanged(nameof(StatusText));
        }, null);
        _logService.Info("UiApp", "create_session_success");
    }

    private async Task JoinSessionAsync()
    {
        // UX: if viewer is already connected, "Подключиться" re-opens the remote screen window
        // instead of doing a second join attempt (codes are one-time anyway).
        if (_role == ConnectionRole.Viewer && _signalingClient is not null)
        {
            _uiContext.Post(_ => RemoteScreenWindowRequested?.Invoke(), null);
            StatusText = "Окно удалённого экрана открыто";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        StatusText = "Подключение к сессии...";
        OnPropertyChanged(nameof(StatusText));
        _logService.Info("UiApp", "join_session_clicked");

        if (JoinLoginCode.Length != 8 || JoinPassCode.Length != 8)
        {
            StatusText = "Логин и пароль должны быть по 8 цифр";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        var response = await _sessionApiClient.JoinSessionAsync(ServerApiBaseUrl, JoinLoginCode, JoinPassCode);
        if (response is null)
        {
            StatusText = "Ошибка подключения";
            OnPropertyChanged(nameof(StatusText));
            _logService.Warn("UiApp", "join_session_failed");
            return;
        }

        // Viewer mode: админ -> смотрит экран host и отправляет input.
        _role = ConnectionRole.Viewer;
        if (AutoIceByPriority)
        {
            await ConnectWsWithAutoIcePriorityAsync(response.WsUrl, response.WsToken, response.SessionId, role: _role);
        }
        else
        {
            await ConnectWsAsync(response.WsUrl, response.WsToken, response.SessionId, startAsCaller: true, role: _role);
        }
        _uiContext.Post(_ => RemoteScreenWindowRequested?.Invoke(), null);

        StatusText = $"Подключено: {response.SessionId}";
        OnPropertyChanged(nameof(StatusText));
        _logService.Info("UiApp", "join_session_success");
    }

    private async Task ConnectWsWithAutoIcePriorityAsync(string wsUrlFromServer, string wsToken, string sessionId, ConnectionRole role)
    {
        var attempts = new[]
        {
            new IceAttemptOptions("LAN", 3000, disableStun: true, disableTurn: true, preferRelay: false, expectedRoute: "host"),
            new IceAttemptOptions("srflx", 4000, disableStun: false, disableTurn: true, preferRelay: false, expectedRoute: "srflx"),
            new IceAttemptOptions("relay", 5000, disableStun: false, disableTurn: false, preferRelay: true, expectedRoute: "relay")
        };

        foreach (var attempt in attempts)
        {
            StatusText = $"Авто ICE: {attempt.Name} ({attempt.TimeoutMs / 1000}с)...";
            OnPropertyChanged(nameof(StatusText));
            _logService.Info("WebRTC", $"auto_ice_attempt_{attempt.Name}");

            await ConnectWsAsync(wsUrlFromServer, wsToken, sessionId, startAsCaller: true, role: role, attempt);

            var succeeded = await WaitForAutoIceSuccessAsync(attempt, _lifetimeCts.Token);
            if (succeeded)
            {
                _logService.Info("WebRTC", $"auto_ice_connected_{attempt.Name}");
                return;
            }

            _logService.Warn("WebRTC", $"auto_ice_timeout_{attempt.Name}");
            await CleanupConnectionAsync().ConfigureAwait(false);
        }

        StatusText = "Авто ICE: fallback к стандартному режиму";
        OnPropertyChanged(nameof(StatusText));
        await ConnectWsAsync(wsUrlFromServer, wsToken, sessionId, startAsCaller: true, role: role);
    }

    private async Task<bool> WaitForAutoIceSuccessAsync(IceAttemptOptions attempt, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while ((DateTime.UtcNow - started).TotalMilliseconds < attempt.TimeoutMs && !ct.IsCancellationRequested)
        {
            if (_firstRemoteFrameTcs?.Task.IsCompletedSuccessfully == true)
            {
                return true;
            }

            var route = InferRouteType(_localIceCandidateType, _remoteIceCandidateType);
            if (IsRouteAcceptableForAttempt(route, attempt.ExpectedRoute))
            {
                return true;
            }

            await Task.Delay(200, ct);
        }

        return false;
    }

    private static bool IsRouteAcceptableForAttempt(string route, string expectedRoute)
    {
        return expectedRoute switch
        {
            "host" => string.Equals(route, "host", StringComparison.OrdinalIgnoreCase),
            "srflx" => string.Equals(route, "host", StringComparison.OrdinalIgnoreCase) || string.Equals(route, "srflx", StringComparison.OrdinalIgnoreCase),
            "relay" => !string.Equals(route, "unknown", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsCandidateAllowedForAttempt(string candidate, IceAttemptOptions? attempt)
    {
        if (attempt is null)
        {
            return true;
        }

        var type = GetIceCandidateType(candidate);
        return attempt.ExpectedRoute switch
        {
            "host" => string.Equals(type, "host", StringComparison.OrdinalIgnoreCase),
            "srflx" => string.Equals(type, "host", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "srflx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "prflx", StringComparison.OrdinalIgnoreCase),
            "relay" => true,
            _ => true
        };
    }

    private async Task ConnectWsAsync(string wsUrlFromServer, string wsToken, string sessionId, bool startAsCaller, ConnectionRole role, IceAttemptOptions? attempt = null)
    {
        // Ensure previous connection is fully cleaned up before starting a new one.
        await CleanupConnectionAsync().ConfigureAwait(false);
        _lifetimeCts = new CancellationTokenSource();

        // Critical: cleanup resets role to None; restore the intended role for this connection.
        _role = role;

        _currentSessionId = sessionId;
        _callerStartDeferred = false;
        _callerStarted = false;
        _hostPeerStateAnnouncedBack = false;
        _hostVideoStarted = false;
        _remoteScreenMeta = null;
        _localScreenMeta = null;
        _lastSentMouseX = null;
        _lastSentMouseY = null;
        _lastMouseMoveSentAtMs = 0;
        _localIceCandidateType = "unknown";
        _remoteIceCandidateType = "unknown";
        _localIceCandidateIp = "n/a";
        _remoteIceCandidateIp = "n/a";
        _iceRecentCandidates.Clear();
        IceRecentCandidatesText = string.Empty;
        _firstRemoteFrameTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IcePathText = "ICE: gathering...";
        IcePathBrush = Brushes.SlateGray;

        var wsUrl = wsUrlFromServer;
        if (string.IsNullOrWhiteSpace(wsUrl) || wsUrl == "/ws")
        {
            wsUrl = WebSocketUrl;
        }

        _signalingClient = new WebSocketSignalingClient();
        _signalingClient.MessageReceived += OnSignalingMessageReceived;
        _signalingClient.Disconnected += OnSignalingDisconnected;
        IPeerConnectionAgent peerAgent;
        IDataChannelAgent dataAgent;
        try
        {
            if (PreferLanVpnNoTurn)
            {
                _logService.Info("WebRTC", "lan_vpn_mode_no_turn_enabled");
            }
            var effectiveStunUrl = attempt?.DisableStun == true ? string.Empty : StunUrl;
            var effectiveTurnUrl = (PreferLanVpnNoTurn || attempt?.DisableTurn == true) ? string.Empty : TurnUrl;
            var effectivePreferRelay = attempt?.PreferRelay ?? PreferRelay;
            var effectiveLanNoTurn = PreferLanVpnNoTurn || attempt?.DisableTurn == true;
            _realPeerAgent = new MixedRealityPeerConnectionAgent(new TransportSettings
            {
                StunUrl = effectiveStunUrl,
                TurnUrl = effectiveTurnUrl,
                TurnUsername = TurnUsername,
                TurnPassword = TurnPassword,
                PreferRelay = effectivePreferRelay,
                PreferLanVpnNoTurn = effectiveLanNoTurn
            }, msg => _logService.Debug("WebRTC", msg));
            var realDataAgent = new MixedRealityDataChannelAgent();
            _realPeerAgent.DataChannelAdded += ch => realDataAgent.AttachChannel(ch);
            await _realPeerAgent.InitializeAsync();
            // Важно: host НЕ должен начинать захват/стрим сразу после "Создать сессию".
            // Стартуем локальное видео только когда реально подключился viewer.

            peerAgent = _realPeerAgent;
            dataAgent = realDataAgent;
            _logService.Info("UiApp", "webrtc_real_agent_enabled");
        }
        catch (Exception ex)
        {
            peerAgent = new MockPeerConnectionAgent();
            await peerAgent.InitializeAsync();
            dataAgent = new MockDataChannelAgent();
            _logService.Warn("UiApp", "webrtc_real_agent_failed_fallback_mock");
            _logService.Error("UiApp", "webrtc_real_agent_error", ex.Message);
        }

        peerAgent.RemoteVideoFrameReceived += OnRemoteVideoFrameReceived;

        _signalingCoordinator = new SignalingCoordinator(
            _signalingClient,
            peerAgent,
            msg => _logService.Debug("Signaling", msg),
            beforeCreateAnswerAsync: async () =>
            {
                // Critical: attach local video BEFORE creating an answer, otherwise video gets negotiated as Inactive.
                if (_role == ConnectionRole.Host && !_hostVideoStarted && _realPeerAgent is not null)
                {
                    _hostVideoStarted = true;
                    await ConfigureLocalVideoAsync(_realPeerAgent);
                    _logService.Info("ScreenCapture", "local_video_started_on_offer");
                }
            },
            shouldSendLocalIceCandidate: candidate => IsCandidateAllowedForAttempt(candidate, attempt),
            shouldAcceptRemoteIceCandidate: candidate => IsCandidateAllowedForAttempt(candidate, attempt));
        _signalingCoordinator.IceCandidateObserved += OnIceCandidateObserved;
        _signalingCoordinator.SetSession(sessionId);
        _dataChannelCoordinator = new DataChannelCoordinator(
            dataAgent,
            msg => _logService.Debug("DataChannel", msg));
        if (role == ConnectionRole.Host)
        {
            _dataChannelCoordinator.MouseReceived += OnMouseInputReceived;
            _dataChannelCoordinator.KeyboardReceived += OnKeyboardInputReceived;
        }
        _dataChannelCoordinator.ScreenMetaReceived += payload =>
        {
            _remoteScreenMeta = payload;
            _logService.Info("RemoteScreen", $"screen_meta_received_{payload.CaptureX}_{payload.CaptureY}_{payload.Width}x{payload.Height}_{payload.DisplayId}");
        };
        _dataChannelCoordinator.HostVideoSettingsRequestReceived += payload =>
        {
            _ = ApplyHostVideoSettingsRequestAsync(payload);
        };
        _dataChannelCoordinator.HostDisplaysReceived += payload =>
            OnHostDisplaysReceived(payload);
        _dataChannelCoordinator.HostDisplaysRequestReceived += payload =>
        {
            _ = SendHostDisplaysIfHostAsync(_lifetimeCts.Token);
        };
        _dataChannelCoordinator.CursorShapeReceived += payload =>
            OnCursorShapeReceived(payload);
        _dataChannelCoordinator.ClipboardReceived += payload =>
            OnClipboardReceived(payload);
        _dataChannelCoordinator.FileMetaReceived += payload =>
            OnFileMetaReceived(payload);
        _dataChannelCoordinator.FileChunkReceived += payload =>
            OnFileChunkReceived(payload);
        _dataChannelCoordinator.FileEndReceived += payload =>
            OnFileEndReceived(payload);

        try
        {
            await _signalingClient.ConnectAsync(wsUrl, sessionId, wsToken);
            _logService.Info("UiApp", "ws_connected");
            await _signalingClient.SendAsync("peer_state", sessionId, new { state = "joined" });
            if (startAsCaller)
            {
                // Our WS server is a pure relay (no queue). If caller sends offer before the other peer connects,
                // the offer is lost. So we defer offer until we see a peer_state from the other side.
                _callerStartDeferred = role == ConnectionRole.Viewer;
                if (!_callerStartDeferred)
                {
                    await _signalingCoordinator.StartAsCallerAsync(sessionId);
                    _callerStarted = true;
                }
                else
                {
                    _logService.Info("Signaling", "caller_deferred_wait_peer_state");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(1500, _lifetimeCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        if (_lifetimeCts.IsCancellationRequested)
                        {
                            return;
                        }

                        if (!_callerStarted && _signalingCoordinator is not null)
                        {
                            try
                            {
                                await _signalingCoordinator.StartAsCallerAsync(_currentSessionId, _lifetimeCts.Token);
                                _callerStarted = true;
                                _logService.Info("Signaling", "caller_started_by_timeout");
                            }
                            catch (OperationCanceledException)
                            {
                                // ignore
                            }
                            catch (Exception ex)
                            {
                                _logService.Warn("Signaling", "caller_start_timeout_failed_" + ex.Message);
                            }
                        }
                    }, _lifetimeCts.Token);
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Error("UiApp", "ws_connect_failed", ex.Message);
            StatusText = "WS подключение не удалось";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        // Init messages over data channels: SCTP/DataChannel может открыться чуть позже, чем WS и SDP/ICE.
        _ = Task.Run(() => SendInitDataChannelMessagesWithRetryAsync(role, _lifetimeCts.Token), _lifetimeCts.Token);
        StartClipboardSyncLoopIfNeeded();
        StartCursorSyncLoopIfNeeded();
    }

    private async Task CleanupConnectionAsync()
    {
        // Cancel background tasks first.
        try
        {
            _lifetimeCts.Cancel();
        }
        catch
        {
            // ignore
        }

        // Unsubscribe handlers to avoid leaks / duplicated callbacks on reconnect.
        try
        {
            if (_signalingClient is not null)
            {
                _signalingClient.MessageReceived -= OnSignalingMessageReceived;
                _signalingClient.Disconnected -= OnSignalingDisconnected;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            if (_realPeerAgent is not null)
            {
                _realPeerAgent.RemoteVideoFrameReceived -= OnRemoteVideoFrameReceived;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            if (_signalingCoordinator is not null)
            {
                _signalingCoordinator.IceCandidateObserved -= OnIceCandidateObserved;
            }
        }
        catch
        {
            // ignore
        }

        // Dispose WS client asynchronously (do not block UI thread).
        var ws = _signalingClient;
        _signalingClient = null;
        if (ws is not null)
        {
            try
            {
                await ws.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            _realPeerAgent?.Dispose();
        }
        catch
        {
            // ignore
        }

        _dataChannelCoordinator = null;
        _signalingCoordinator = null;
        _realPeerAgent = null;
        _role = ConnectionRole.None;
        _currentSessionId = string.Empty;
        _localIceCandidateType = "unknown";
        _remoteIceCandidateType = "unknown";
        _localIceCandidateIp = "n/a";
        _remoteIceCandidateIp = "n/a";
        _iceRecentCandidates.Clear();
        IceRecentCandidatesText = string.Empty;
        _firstRemoteFrameTcs = null;
        IcePathText = "ICE: unknown";
        IcePathBrush = Brushes.SlateGray;
        Interlocked.Exchange(ref _clipboardLoopStarted, 0);
        Interlocked.Exchange(ref _cursorLoopStarted, 0);
        _lastObservedClipboardText = string.Empty;
        _lastSentClipboardText = string.Empty;
        _lastAppliedRemoteClipboardText = string.Empty;
        _lastAppliedRemoteClipboardAtUtc = DateTime.MinValue;
        _lastSentCursorType = string.Empty;
        _uiContext.Post(_ =>
        {
            RemoteCursor = Cursors.Arrow;
            ViewerAvailableDisplayIds.Clear();
            RefreshViewerDisplayIdsFallback();
        }, null);
        CleanupIncomingFileTransfers();

        try
        {
            _lifetimeCts.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private async Task TryCloseCurrentSessionAsync()
    {
        var sessionId = _currentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            var ok = await _sessionApiClient.CloseSessionAsync(ServerApiBaseUrl, sessionId).ConfigureAwait(false);
            if (ok)
            {
                _logService.Info("UiApp", "session_closed_on_shutdown");
            }
            else
            {
                _logService.Warn("UiApp", "session_close_on_shutdown_failed");
            }
        }
        catch (Exception ex)
        {
            _logService.Warn("UiApp", "session_close_on_shutdown_exception_" + ex.Message);
        }
    }

    private async Task SendInitDataChannelMessagesWithRetryAsync(ConnectionRole role, CancellationToken ct)
    {
        var dc = _dataChannelCoordinator;
        if (dc is null)
        {
            return;
        }

        // Viewer не обязан отправлять screen_meta/clipboard на старте.
        var shouldSendScreenMeta = role == ConnectionRole.Host && _localScreenMeta is not null;
        var shouldSendHostDisplays = role == ConnectionRole.Host;
        var shouldRequestHostDisplays = role == ConnectionRole.Viewer;
        var shouldSendClipboardInit = true;

        for (var attempt = 1; attempt <= 20; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var allOk = true;
            try
            {
                if (shouldSendScreenMeta && _localScreenMeta is not null)
                {
                    await dc.SendScreenMetaAsync(_localScreenMeta, ct);
                    shouldSendScreenMeta = false;
                    _logService.Debug("RemoteScreen", "screen_meta_sent");
                }
            }
            catch (Exception ex)
            {
                allOk = false;
                _logService.Debug("DataChannel", $"init_send_screen_meta_failed_attempt_{attempt}_" + ex.Message);
            }

            try
            {
                if (shouldSendHostDisplays)
                {
                    await SendHostDisplaysIfHostAsync(ct);
                    shouldSendHostDisplays = false;
                    _logService.Debug("DataChannel", "host_displays_sent");
                }
            }
            catch (Exception ex)
            {
                allOk = false;
                _logService.Debug("DataChannel", $"init_send_host_displays_failed_attempt_{attempt}_" + ex.Message);
            }

            try
            {
                if (shouldRequestHostDisplays)
                {
                    await dc.SendHostDisplaysRequestAsync(new HostDisplaysRequestPayload
                    {
                        RequestId = Guid.NewGuid().ToString("N")
                    }, ct);
                    shouldRequestHostDisplays = false;
                    _logService.Debug("DataChannel", "host_displays_requested");
                }
            }
            catch (Exception ex)
            {
                allOk = false;
                _logService.Debug("DataChannel", $"init_request_host_displays_failed_attempt_{attempt}_" + ex.Message);
            }

            try
            {
                if (shouldSendClipboardInit)
                {
                    await dc.SendClipboardAsync(new ClipboardTextPayload
                    {
                        Text = "zconect-init",
                        OriginPeerId = "local"
                    }, ct);
                    shouldSendClipboardInit = false;
                    _logService.Debug("DataChannel", "clipboard_init_sent");
                }
            }
            catch (Exception ex)
            {
                allOk = false;
                _logService.Debug("DataChannel", $"init_send_clipboard_failed_attempt_{attempt}_" + ex.Message);
            }

            if (allOk || (!shouldSendScreenMeta && !shouldSendHostDisplays && !shouldRequestHostDisplays && !shouldSendClipboardInit))
            {
                return;
            }

            try
            {
                await Task.Delay(250, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnSignalingMessageReceived(SignalingMessage msg)
    {
        _logService.Debug("UiApp", $"ws_message_{msg.Type}");
        if (msg.Type == "peer_state")
        {
            // Host may have connected before viewer; re-announce host state when we notice the other side joined.
            if (_role == ConnectionRole.Host && !_hostPeerStateAnnouncedBack && _signalingClient is not null)
            {
                _hostPeerStateAnnouncedBack = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _signalingClient.SendAsync("peer_state", _currentSessionId, new { state = "host_ready" }, _lifetimeCts.Token);
                        _logService.Info("Signaling", "host_peer_state_reannounced");
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore
                    }
                    catch (Exception ex)
                    {
                        _logService.Warn("Signaling", "host_peer_state_reannounce_failed_" + ex.Message);
                    }
                }, _lifetimeCts.Token);
            }

            // Видео на host стартуем не по peer_state, а перед созданием answer (см. beforeAnswerOfferAsync),
            // чтобы negotiated direction не ушел в Inactive.

            // Start offer when we detect the other peer is online.
            if (_callerStartDeferred && !_callerStarted && _signalingCoordinator is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _signalingCoordinator.StartAsCallerAsync(_currentSessionId, _lifetimeCts.Token);
                        _callerStarted = true;
                        _logService.Info("Signaling", "caller_started_by_peer_state");
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore
                    }
                    catch (Exception ex)
                    {
                        _logService.Warn("Signaling", "caller_start_peer_state_failed_" + ex.Message);
                    }
                }, _lifetimeCts.Token);
            }

            _uiContext.Post(_ =>
            {
                StatusText = $"WS: {msg.Type}";
                OnPropertyChanged(nameof(StatusText));
            }, null);
        }
    }

    private void OnSignalingDisconnected()
    {
        _logService.Warn("UiApp", "ws_disconnected");
        _uiContext.Post(_ =>
        {
            StatusText = "WS отключен";
            OnPropertyChanged(nameof(StatusText));
        }, null);
    }

    private void OnIceCandidateObserved(string direction, string candidateType, string candidateIp, int candidatePort)
    {
        _uiContext.Post(_ =>
        {
            if (string.Equals(direction, "local", StringComparison.Ordinal))
            {
                var shouldPromote = IceTypeRank(candidateType) >= IceTypeRank(_localIceCandidateType);
                _localIceCandidateType = SelectPreferredIceType(_localIceCandidateType, candidateType);
                if (shouldPromote && !string.IsNullOrWhiteSpace(candidateIp))
                {
                    _localIceCandidateIp = candidateIp;
                }
            }
            else
            {
                var shouldPromote = IceTypeRank(candidateType) >= IceTypeRank(_remoteIceCandidateType);
                _remoteIceCandidateType = SelectPreferredIceType(_remoteIceCandidateType, candidateType);
                if (shouldPromote && !string.IsNullOrWhiteSpace(candidateIp))
                {
                    _remoteIceCandidateIp = candidateIp;
                }
            }

            var route = InferRouteType(_localIceCandidateType, _remoteIceCandidateType);
            IcePathText = $"ICE: {route} [hint] (local:{_localIceCandidateType}/{_localIceCandidateIp}, remote:{_remoteIceCandidateType}/{_remoteIceCandidateIp})";
            IcePathBrush = GetIcePathBrush(route);

            var item = $"{DateTime.Now:HH:mm:ss} {direction}:{candidateType} {candidateIp}:{candidatePort}";
            _iceRecentCandidates.Enqueue(item);
            while (_iceRecentCandidates.Count > 5)
            {
                _iceRecentCandidates.Dequeue();
            }
            IceRecentCandidatesText = string.Join(Environment.NewLine, _iceRecentCandidates.Reverse());
        }, null);
    }

    private static string SelectPreferredIceType(string current, string incoming)
    {
        if (IceTypeRank(incoming) > IceTypeRank(current))
        {
            return incoming;
        }
        return current;
    }

    private static string InferRouteType(string localType, string remoteType)
    {
        if (string.Equals(localType, "relay", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remoteType, "relay", StringComparison.OrdinalIgnoreCase))
        {
            return "relay";
        }
        if (string.Equals(localType, "srflx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remoteType, "srflx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(localType, "prflx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remoteType, "prflx", StringComparison.OrdinalIgnoreCase))
        {
            return "srflx";
        }
        if (string.Equals(localType, "host", StringComparison.OrdinalIgnoreCase)
            && string.Equals(remoteType, "host", StringComparison.OrdinalIgnoreCase))
        {
            return "host";
        }
        return "unknown";
    }

    private static int IceTypeRank(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "relay" => 4,
            "srflx" => 3,
            "prflx" => 2,
            "host" => 1,
            _ => 0
        };
    }

    private static string GetIceCandidateType(string candidate)
    {
        if (candidate.Contains(" typ relay", StringComparison.OrdinalIgnoreCase))
        {
            return "relay";
        }
        if (candidate.Contains(" typ srflx", StringComparison.OrdinalIgnoreCase))
        {
            return "srflx";
        }
        if (candidate.Contains(" typ prflx", StringComparison.OrdinalIgnoreCase))
        {
            return "prflx";
        }
        if (candidate.Contains(" typ host", StringComparison.OrdinalIgnoreCase))
        {
            return "host";
        }
        return "unknown";
    }

    private static Cursor MapRemoteCursor(string cursorType)
    {
        return cursorType.ToLowerInvariant() switch
        {
            "ibeam" => Cursors.IBeam,
            "hand" => Cursors.Hand,
            "wait" => Cursors.Wait,
            "appstarting" => Cursors.AppStarting,
            "cross" => Cursors.Cross,
            "sizewe" => Cursors.SizeWE,
            "sizens" => Cursors.SizeNS,
            "sizenwse" => Cursors.SizeNWSE,
            "sizenesw" => Cursors.SizeNESW,
            "sizeall" => Cursors.SizeAll,
            "no" => Cursors.No,
            _ => Cursors.Arrow
        };
    }

    private static Brush GetIcePathBrush(string route)
    {
        return route.ToLowerInvariant() switch
        {
            "host" => Brushes.ForestGreen,
            "srflx" => Brushes.Goldenrod,
            "relay" => Brushes.DarkOrange,
            _ => Brushes.SlateGray
        };
    }

    private void OnRemoteVideoFrameReceived(RemoteVideoFrame frame)
    {
        _uiContext.Post(_ =>
        {
            if (_remoteFrameBitmap is null)
            {
                _logService.Info("RemoteScreen", "first_remote_video_frame_received");
                _firstRemoteFrameTcs?.TrySetResult(true);
            }

            if (_remoteFrameBitmap is null ||
                _remoteFrameBitmap.PixelWidth != frame.Width ||
                _remoteFrameBitmap.PixelHeight != frame.Height)
            {
                _remoteFrameBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                RemoteFrameImage = _remoteFrameBitmap;
                OnPropertyChanged(nameof(RemoteFrameImage));
            }

            _remoteFrameBitmap.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height),
                frame.Buffer,
                frame.Stride,
                0);
        }, null);
    }

    private void SaveSettings()
    {
        ApplyDebugLogFilter();
        RefreshAvailableDisplayIds();
        _settingsService.Save(_settings);
        StatusText = "Settings saved";
        OnPropertyChanged(nameof(StatusText));
        _logService.Info("UiApp", "settings_saved");
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

    private void RefreshAvailableDisplayIds()
    {
        var ids = DisplayEnumerator.GetDisplays()
            .Select(d => d.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
        {
            ids.Add("DISPLAY1");
        }

        AvailableDisplayIds.Clear();
        foreach (var id in ids)
        {
            AvailableDisplayIds.Add(id);
        }

        if (string.IsNullOrWhiteSpace(DisplayId) || !ids.Contains(DisplayId, StringComparer.OrdinalIgnoreCase))
        {
            DisplayId = ids[0];
        }
    }

    private void RefreshViewerDisplayIdsFallback()
    {
        if (ViewerAvailableDisplayIds.Count == 0)
        {
            ViewerAvailableDisplayIds.Clear();
            ViewerAvailableDisplayIds.Add("DISPLAY1");
        }

        if (string.IsNullOrWhiteSpace(ViewerRequestedDisplayId))
        {
            ViewerRequestedDisplayId = ViewerAvailableDisplayIds[0];
            OnPropertyChanged(nameof(ViewerRequestedDisplayId));
        }
    }

    private void OnHostDisplaysReceived(HostDisplaysPayload payload)
    {
        if (_role != ConnectionRole.Viewer)
        {
            return;
        }

        var ids = (payload?.Displays ?? new List<DisplayInfoPayload>())
            .Select(d => d.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            ids.Add("DISPLAY1");
        }

        _uiContext.Post(_ =>
        {
            ViewerAvailableDisplayIds.Clear();
            foreach (var id in ids)
            {
                ViewerAvailableDisplayIds.Add(id);
            }

            if (string.IsNullOrWhiteSpace(ViewerRequestedDisplayId)
                || !ids.Contains(ViewerRequestedDisplayId, StringComparer.OrdinalIgnoreCase))
            {
                ViewerRequestedDisplayId = ids[0];
                OnPropertyChanged(nameof(ViewerRequestedDisplayId));
            }
        }, null);
    }

    private async Task SendHostDisplaysIfHostAsync(CancellationToken ct)
    {
        if (_role != ConnectionRole.Host)
        {
            return;
        }

        var dc = _dataChannelCoordinator;
        if (dc is null)
        {
            return;
        }

        var displays = DisplayEnumerator.GetDisplays()
            .Select(d => new DisplayInfoPayload
            {
                Id = d.Id,
                Name = d.Name ?? string.Empty,
                IsPrimary = d.IsPrimary
            })
            .ToList();
        if (displays.Count == 0)
        {
            displays.Add(new DisplayInfoPayload
            {
                Id = "DISPLAY1",
                Name = "DISPLAY1",
                IsPrimary = true
            });
        }

        await dc.SendHostDisplaysAsync(new HostDisplaysPayload
        {
            Displays = displays
        }, ct);
    }

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

    private void RefreshUacPolicyState()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UacPolicyRegistryPath, writable: false);
            var raw = key?.GetValue(UacPromptOnSecureDesktopValueName);
            var value = raw switch
            {
                int i => i,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => 1
            };

            UacPromptOnSecureDesktopEnabled = value != 0;
        }
        catch (Exception ex)
        {
            UacPromptOnSecureDesktopEnabled = true;
            _logService.Warn("UiApp", "uac_policy_read_failed_" + ex.Message);
        }
    }

    private async Task ToggleUacSecureDesktopPolicyAsync()
    {
        var disableSecureDesktop = UacPromptOnSecureDesktopEnabled;
        var warning = disableSecureDesktop
            ? "Отключить 'защищенный рабочий стол' для UAC?\n\nЭто позволит видеть запросы UAC в удаленной сессии, но снижает безопасность локального ПК."
            : "Включить обратно 'защищенный рабочий стол' для UAC?\n\nЭто повысит безопасность, но запросы UAC могут перестать отображаться в удаленной сессии.";
        var result = MessageBox.Show(warning, "UAC политика", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UacPolicyRegistryPath, writable: true);
            if (key is null)
            {
                StatusText = "Не удалось открыть UAC политику (реестр)";
                OnPropertyChanged(nameof(StatusText));
                _logService.Warn("UiApp", "uac_policy_open_failed");
                return;
            }

            key.SetValue(UacPromptOnSecureDesktopValueName, disableSecureDesktop ? 0 : 1, RegistryValueKind.DWord);
            RefreshUacPolicyState();

            StatusText = disableSecureDesktop
                ? "UAC: показ в удаленной сессии разрешен"
                : "UAC: защищенный рабочий стол включен";
            OnPropertyChanged(nameof(StatusText));
            _logService.Info("UiApp", disableSecureDesktop ? "uac_secure_desktop_disabled" : "uac_secure_desktop_enabled");
        }
        catch (Exception ex)
        {
            StatusText = "Не удалось изменить UAC политику";
            OnPropertyChanged(nameof(StatusText));
            _logService.Error("UiApp", "uac_policy_write_failed", ex.Message);
        }

        await Task.CompletedTask;
    }

    private async Task SendFileAsync()
    {
        var dc = _dataChannelCoordinator;
        if (dc is null)
        {
            StatusText = "Подключение не активно";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        var openFileDialog = new OpenFileDialog
        {
            Multiselect = false,
            CheckFileExists = true,
            Title = "Выберите файл для отправки"
        };

        if (openFileDialog.ShowDialog() != true)
        {
            return;
        }

        var filePath = openFileDialog.FileName;
        if (!File.Exists(filePath))
        {
            StatusText = "Файл не найден";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        try
        {
            var transferId = Guid.NewGuid().ToString("N");
            var fileInfo = new FileInfo(filePath);
            var fileName = fileInfo.Name;
            long sentBytes = 0;
            var lastProgress = -1;

            StatusText = "Отправка файла...";
            OnPropertyChanged(nameof(StatusText));
            UpdateFileTransferProgress("Отправка", fileName, 0, fileInfo.Length);

            var hash = await ComputeSha256Async(filePath, _lifetimeCts.Token);
            await dc.SendFileMetaAsync(new FileMetaPayload
            {
                TransferId = transferId,
                FileName = fileName,
                FileSize = fileInfo.Length,
                MimeType = "application/octet-stream",
                Hash = hash
            }, _lifetimeCts.Token);

            var sequence = 0;
            var buffer = new byte[FileChunkSizeBytes];
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            while (true)
            {
                var read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), _lifetimeCts.Token);
                if (read <= 0)
                {
                    break;
                }

                var base64 = Convert.ToBase64String(buffer, 0, read);
                await dc.SendFileChunkAsync(new FileChunkPayload
                {
                    TransferId = transferId,
                    Sequence = sequence++,
                    Base64Data = base64
                }, _lifetimeCts.Token);

                // Keep dc-input responsive during large file transfer.
                if ((sequence & 0x7) == 0)
                {
                    await Task.Delay(1, _lifetimeCts.Token);
                }

                sentBytes += read;
                if (fileInfo.Length > 0)
                {
                    var progress = (int)(sentBytes * 100 / fileInfo.Length);
                    if (progress != lastProgress)
                    {
                        lastProgress = progress;
                        UpdateFileTransferProgress("Отправка", fileName, sentBytes, fileInfo.Length);
                    }
                }
            }

            await dc.SendFileEndAsync(new FileEndPayload
            {
                TransferId = transferId
            }, _lifetimeCts.Token);

            StatusText = "Файл отправлен: " + fileName;
            OnPropertyChanged(nameof(StatusText));
            UpdateFileTransferProgress("Отправка", fileName, fileInfo.Length, fileInfo.Length);
            _logService.Info("FileTransfer", "file_sent_" + fileName + "_" + fileInfo.Length);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            StatusText = "Не удалось отправить файл";
            OnPropertyChanged(nameof(StatusText));
            FileTransferProgressText = "Передача файлов: ошибка отправки";
            _logService.Error("FileTransfer", "file_send_failed", ex.Message);
        }
    }

    private void OnFileMetaReceived(FileMetaPayload payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.TransferId))
        {
            return;
        }

        var safeFileName = Path.GetFileName(payload.FileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            safeFileName = "received-" + payload.TransferId + ".bin";
        }

        try
        {
            var tempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZConnect",
                "IncomingTemp");
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, payload.TransferId + ".part");

            var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var state = new IncomingFileTransferState(payload.TransferId, safeFileName, tempPath, payload.Hash ?? string.Empty, payload.FileSize, stream);

            if (_incomingFileTransfers.TryRemove(payload.TransferId, out var oldState))
            {
                oldState.Dispose();
            }

            _incomingFileTransfers[payload.TransferId] = state;
            _logService.Info("FileTransfer", "file_meta_received_" + safeFileName + "_" + payload.FileSize);
            _uiContext.Post(_ =>
            {
                StatusText = "Получаем файл: " + safeFileName;
                OnPropertyChanged(nameof(StatusText));
                UpdateFileTransferProgress("Прием", safeFileName, 0, payload.FileSize);
            }, null);
        }
        catch (Exception ex)
        {
            _logService.Error("FileTransfer", "file_meta_receive_failed", ex.Message);
        }
    }

    private void OnFileChunkReceived(FileChunkPayload payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.TransferId))
        {
            return;
        }

        if (!_incomingFileTransfers.TryGetValue(payload.TransferId, out var state))
        {
            _logService.Warn("FileTransfer", "file_chunk_without_meta_" + payload.TransferId);
            return;
        }

        try
        {
            if (payload.Sequence != state.NextSequence)
            {
                _logService.Warn("FileTransfer", "file_chunk_sequence_mismatch_" + payload.TransferId);
                AbortIncomingTransfer(payload.TransferId, state, true);
                return;
            }

            var chunkBytes = Convert.FromBase64String(payload.Base64Data ?? string.Empty);
            lock (state.SyncRoot)
            {
                state.Stream.Write(chunkBytes, 0, chunkBytes.Length);
                state.NextSequence++;
                state.BytesWritten += chunkBytes.Length;
            }

            UpdateFileTransferProgress("Прием", state.FileName, state.BytesWritten, state.ExpectedSize);
        }
        catch (Exception ex)
        {
            _logService.Error("FileTransfer", "file_chunk_receive_failed", ex.Message);
            AbortIncomingTransfer(payload.TransferId, state, true);
        }
    }

    private void OnFileEndReceived(FileEndPayload payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.TransferId))
        {
            return;
        }

        if (!_incomingFileTransfers.TryRemove(payload.TransferId, out var state))
        {
            _logService.Warn("FileTransfer", "file_end_without_transfer_" + payload.TransferId);
            return;
        }

        try
        {
            lock (state.SyncRoot)
            {
                state.Stream.Flush();
            }
            state.Dispose();

            if (!string.IsNullOrWhiteSpace(state.ExpectedHash))
            {
                var actualHash = ComputeSha256(state.TargetPath);
                if (!string.Equals(actualHash, state.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logService.Warn("FileTransfer", "file_hash_mismatch_" + state.FileName);
                    _uiContext.Post(_ =>
                    {
                        StatusText = "Файл получен, но hash не совпал: " + state.FileName;
                        OnPropertyChanged(nameof(StatusText));
                    }, null);
                    return;
                }
            }

            var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ZConnectReceived");
            Directory.CreateDirectory(targetDir);
            var finalTargetPath = GetUniqueFilePath(targetDir, state.FileName);
            File.Move(state.TargetPath, finalTargetPath, overwrite: true);

            _logService.Info("FileTransfer", "file_received_ok_" + state.FileName + "_" + state.BytesWritten);
            _uiContext.Post(_ =>
            {
                StatusText = "Файл получен: " + Path.GetFileName(finalTargetPath);
                OnPropertyChanged(nameof(StatusText));
                UpdateFileTransferProgress("Прием", state.FileName, state.ExpectedSize > 0 ? state.ExpectedSize : state.BytesWritten, state.ExpectedSize);
            }, null);
        }
        catch (Exception ex)
        {
            _logService.Error("FileTransfer", "file_end_receive_failed", ex.Message);
        }
    }

    private void AbortIncomingTransfer(string transferId, IncomingFileTransferState state, bool deletePartialFile)
    {
        _incomingFileTransfers.TryRemove(transferId, out _);
        try
        {
            state.Dispose();
            if (deletePartialFile && File.Exists(state.TargetPath))
            {
                File.Delete(state.TargetPath);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void CleanupIncomingFileTransfers()
    {
        foreach (var pair in _incomingFileTransfers.ToArray())
        {
            AbortIncomingTransfer(pair.Key, pair.Value, true);
        }
        _incomingFileTransfers.Clear();
    }

    private void UpdateFileTransferProgress(string direction, string fileName, long currentBytes, long totalBytes)
    {
        _uiContext.Post(_ =>
        {
            if (totalBytes <= 0)
            {
                FileTransferProgressText = $"{direction}: {fileName} ({currentBytes} bytes)";
                return;
            }

            var percent = (int)Math.Clamp(currentBytes * 100 / totalBytes, 0, 100);
            FileTransferProgressText = $"{direction}: {fileName} ({percent}%)";
        }, null);
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hashBytes = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hashBytes);
    }

    private static string ComputeSha256(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(fs);
        return Convert.ToHexString(hashBytes);
    }

    private static string GetUniqueFilePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{baseName} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{baseName}-{Guid.NewGuid():N}{ext}");
    }

    private void OnMouseInputReceived(MouseInputPayload payload)
    {
        if (_role != ConnectionRole.Host)
        {
            return;
        }
        try
        {
            _windowsInputInjectionService.InjectMouse(payload);
            _logService.Debug("InputHost", "mouse_injected_" + payload.Action);
        }
        catch (Exception ex)
        {
            _logService.Error("InputHost", "mouse_inject_failed", ex.Message);
        }
    }

    private void OnKeyboardInputReceived(KeyboardInputPayload payload)
    {
        if (_role != ConnectionRole.Host)
        {
            return;
        }
        try
        {
            _windowsInputInjectionService.InjectKeyboard(payload);
            _logService.Debug("InputHost", "keyboard_injected_" + payload.Action);
        }
        catch (Exception ex)
        {
            _logService.Error("InputHost", "keyboard_inject_failed", ex.Message);
        }
    }

    private async Task RunFfmpegProbeAsync()
    {
        StatusText = "FFmpeg VP8 probe...";
        OnPropertyChanged(nameof(StatusText));

        var profile = QualityProfiles.Resolve(QualityPreset);
        var displays = DisplayEnumerator.GetDisplays();
        DisplaySource? selectedDisplay = null;
        if (!string.IsNullOrWhiteSpace(DisplayId))
        {
            selectedDisplay = displays.FirstOrDefault(d => string.Equals(d.Id, DisplayId, StringComparison.OrdinalIgnoreCase));
        }

        selectedDisplay ??= displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        var probe = new FfmpegVp8ProbeService();
        var probeDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe");
        var (ok, message) = await probe.RunProbeAsync(FfmpegPath, probeDir, profile, selectedDisplay);

        StatusText = ok ? "VP8 probe OK" : "VP8 probe failed";
        OnPropertyChanged(nameof(StatusText));
        if (ok)
        {
            _logService.Info("ScreenCapture", message);
        }
        else
        {
            _logService.Error("ScreenCapture", "vp8_probe_failed", message);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private async Task ConfigureLocalVideoAsync(IPeerConnectionAgent peerAgent)
    {
        var profile = QualityProfiles.Resolve(QualityPreset);
        var displays = DisplayEnumerator.GetDisplays();
        if (displays.Count == 0)
        {
            _logService.Warn("ScreenCapture", "no_display_detected_for_local_video");
            return;
        }

        // Always capture full screen; quality preset affects FPS only.
        int captureX;
        int captureY;
        int width;
        int height;
        string displayIdForLog;

        if (string.Equals(DisplayMode?.Trim(), "All", StringComparison.OrdinalIgnoreCase))
        {
            var minX = displays.Min(d => d.X);
            var minY = displays.Min(d => d.Y);
            var maxR = displays.Max(d => d.X + d.Width);
            var maxB = displays.Max(d => d.Y + d.Height);
            captureX = minX;
            captureY = minY;
            width = Math.Max(1, maxR - minX);
            height = Math.Max(1, maxB - minY);
            displayIdForLog = "ALL";
        }
        else
        {
            DisplaySource? selectedDisplay = null;
            if (!string.IsNullOrWhiteSpace(DisplayId))
            {
                selectedDisplay = displays.FirstOrDefault(d => string.Equals(d.Id, DisplayId, StringComparison.OrdinalIgnoreCase));
            }
            selectedDisplay ??= displays.FirstOrDefault(d => d.IsPrimary) ?? displays.First();

            captureX = selectedDisplay.X;
            captureY = selectedDisplay.Y;
            width = Math.Max(1, selectedDisplay.Width);
            height = Math.Max(1, selectedDisplay.Height);
            displayIdForLog = selectedDisplay.Id;
        }

        await peerAgent.ConfigureLocalVideoAsync(new LocalVideoOptions
        {
            CaptureX = captureX,
            CaptureY = captureY,
            Width = width,
            Height = height,
            Fps = profile.Fps
        });
        _logService.Info("ScreenCapture", $"local_video_configured_{displayIdForLog}_{width}x{height}_{profile.Fps}fps");

        // Кешируем meta — отправим после поднятия data channels (когда coordinator готов).
        _localScreenMeta = new ScreenMetaPayload
        {
            CaptureX = captureX,
            CaptureY = captureY,
            Width = width,
            Height = height,
            DisplayId = displayIdForLog
        };
    }

    private async Task SendViewerVideoSettingsRequestAsync(bool quickReconnect)
    {
        if (_role != ConnectionRole.Viewer || _dataChannelCoordinator is null)
        {
            StatusText = "Доступно только для подключившегося клиента";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        var payload = new HostVideoSettingsRequestPayload
        {
            QualityPreset = string.IsNullOrWhiteSpace(ViewerRequestedQualityPreset) ? "Auto" : ViewerRequestedQualityPreset,
            DisplayMode = string.IsNullOrWhiteSpace(ViewerRequestedDisplayMode) ? "Current" : ViewerRequestedDisplayMode,
            DisplayId = ViewerRequestedDisplayId ?? string.Empty,
            QuickReconnect = quickReconnect
        };

        try
        {
            await _dataChannelCoordinator.SendHostVideoSettingsRequestAsync(payload, _lifetimeCts.Token);
            StatusText = quickReconnect ? "Запрошен быстрый реконнект видео" : "Запрошено применение настроек видео";
            OnPropertyChanged(nameof(StatusText));
            _logService.Info("RemoteScreen", $"viewer_settings_request_sent_{payload.QualityPreset}_{payload.DisplayMode}_{payload.DisplayId}_reconnect_{payload.QuickReconnect}");
        }
        catch (Exception ex)
        {
            StatusText = "Не удалось отправить запрос настроек";
            OnPropertyChanged(nameof(StatusText));
            _logService.Warn("RemoteScreen", "viewer_settings_request_send_failed_" + ex.Message);
        }
    }

    private async Task ApplyHostVideoSettingsRequestAsync(HostVideoSettingsRequestPayload payload)
    {
        if (_role != ConnectionRole.Host || _realPeerAgent is null)
        {
            return;
        }

        try
        {
            await _hostVideoSettingsLock.WaitAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var oldQuality = QualityPreset;
        var oldMode = DisplayMode;
        var oldDisplay = DisplayId;

        QualityPreset = string.IsNullOrWhiteSpace(payload.QualityPreset) ? oldQuality : payload.QualityPreset;
        DisplayMode = string.IsNullOrWhiteSpace(payload.DisplayMode) ? oldMode : payload.DisplayMode;
        DisplayId = payload.DisplayId ?? string.Empty;
        SaveSettings();

        try
        {
            await ConfigureLocalVideoAsync(_realPeerAgent);
            if (_dataChannelCoordinator is not null && _localScreenMeta is not null)
            {
                await _dataChannelCoordinator.SendScreenMetaAsync(_localScreenMeta, _lifetimeCts.Token);
            }

            if (payload.QuickReconnect && _signalingCoordinator is not null && _signalingClient is not null)
            {
                // Let capture settings settle before renegotiation.
                await Task.Delay(80, _lifetimeCts.Token);
                await _signalingCoordinator.StartAsCallerAsync(_currentSessionId, _lifetimeCts.Token);
                _logService.Info("Signaling", "host_quick_reconnect_offer_sent");
            }

            _logService.Info("RemoteScreen", $"host_settings_applied_{QualityPreset}_{DisplayMode}_{DisplayId}_reconnect_{payload.QuickReconnect}");
        }
        catch (Exception ex)
        {
            QualityPreset = oldQuality;
            DisplayMode = oldMode;
            DisplayId = oldDisplay;
            SaveSettings();
            _logService.Warn("RemoteScreen", "host_settings_apply_failed_" + ex.Message);
        }
        finally
        {
            _hostVideoSettingsLock.Release();
        }
    }

    private void StartClipboardSyncLoopIfNeeded()
    {
        if (Interlocked.Exchange(ref _clipboardLoopStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() => ClipboardSyncLoopAsync(_lifetimeCts.Token), _lifetimeCts.Token);
    }

    private async Task ClipboardSyncLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var text = await _uiContextInvokeAsync(() =>
                {
                    try
                    {
                        return Clipboard.ContainsText() ? (Clipboard.GetText() ?? string.Empty) : string.Empty;
                    }
                    catch
                    {
                        return string.Empty;
                    }
                });

                if (!string.Equals(text, _lastObservedClipboardText, StringComparison.Ordinal))
                {
                    // Skip sending init ping as real clipboard content.
                    if (string.Equals(text, "zconect-init", StringComparison.Ordinal))
                    {
                        _lastObservedClipboardText = text;
                    }
                    else
                    {
                        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
                        if (bytes > 0 && bytes <= ClipboardMaxBytes)
                        {
                            // Echo protection: do not resend just-applied remote text.
                            var isRecentRemoteEcho = string.Equals(text, _lastAppliedRemoteClipboardText, StringComparison.Ordinal)
                                && (DateTime.UtcNow - _lastAppliedRemoteClipboardAtUtc).TotalMilliseconds < 1500;
                            if (isRecentRemoteEcho || string.Equals(text, _lastSentClipboardText, StringComparison.Ordinal))
                            {
                                _lastObservedClipboardText = text;
                            }
                            else
                            {
                                var dc = _dataChannelCoordinator;
                                if (dc is not null)
                                {
                                    try
                                    {
                                        var originId = _role == ConnectionRole.Viewer ? "viewer" : "host";
                                        await dc.SendClipboardAsync(new ClipboardTextPayload
                                        {
                                            Text = text,
                                            OriginPeerId = originId
                                        }, ct);
                                        _lastSentClipboardText = text;
                                        _lastObservedClipboardText = text;
                                        _logService.Debug("ClipboardSync", "clipboard_sent_len_" + text.Length);
                                    }
                                    catch (Exception ex)
                                    {
                                        // Keep _lastObservedClipboardText unchanged to retry on next poll.
                                        _logService.Debug("ClipboardSync", "clipboard_send_failed_" + ex.Message);
                                    }
                                }
                            }
                        }
                        else
                        {
                            _lastObservedClipboardText = text;
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                await Task.Delay(ClipboardPollMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void StartCursorSyncLoopIfNeeded()
    {
        if (Interlocked.Exchange(ref _cursorLoopStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() => CursorSyncLoopAsync(_lifetimeCts.Token), _lifetimeCts.Token);
    }

    private async Task CursorSyncLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_role == ConnectionRole.Host)
                {
                    var cursorType = CursorShapeService.GetCurrentCursorType();
                    if (!string.Equals(cursorType, _lastSentCursorType, StringComparison.Ordinal))
                    {
                        var dc = _dataChannelCoordinator;
                        if (dc is not null)
                        {
                            await dc.SendCursorShapeAsync(new CursorShapePayload
                            {
                                CursorType = cursorType
                            }, ct);
                            _lastSentCursorType = cursorType;
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                await Task.Delay(120, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnCursorShapeReceived(CursorShapePayload payload)
    {
        if (_role != ConnectionRole.Viewer)
        {
            return;
        }

        var cursor = MapRemoteCursor(payload?.CursorType ?? "arrow");
        _uiContext.Post(_ => RemoteCursor = cursor, null);
    }

    private void OnClipboardReceived(ClipboardTextPayload payload)
    {
        if (payload is null)
        {
            return;
        }

        var text = payload.Text ?? string.Empty;
        // Do not apply init ping to clipboard — it would overwrite user content.
        if (string.Equals(text, "zconect-init", StringComparison.Ordinal))
        {
            _logService.Debug("ClipboardSync", "clipboard_init_ignored");
            return;
        }

        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes > ClipboardMaxBytes)
        {
            _logService.Warn("ClipboardSync", "clipboard_received_too_large_" + bytes);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _uiContextInvokeAsync(() =>
                {
                    try
                    {
                        Clipboard.SetText(text);
                    }
                    catch
                    {
                        // ignore
                    }
                    return true;
                });
                _lastAppliedRemoteClipboardText = text;
                _lastAppliedRemoteClipboardAtUtc = DateTime.UtcNow;
                _lastObservedClipboardText = text;
                _logService.Info("ClipboardSync", "clipboard_text_received_len_" + text.Length);
            }
            catch
            {
                // ignore
            }
        });
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

    // === Viewer-side input capture (Remote screen surface) ===
    public void HandleRemoteSurfaceMouseMove(FrameworkElement surface, MouseEventArgs e)
    {
        if (_role != ConnectionRole.Viewer)
        {
            return;
        }
        if (_dataChannelCoordinator is null)
        {
            return;
        }

        // Send cursor moves even without mouse down (feels more like a real RDP).
        // Throttle to avoid flooding.
        var nowMs = Environment.TickCount64;
        if (nowMs - _lastMouseMoveSentAtMs < 16)
        {
            return;
        }
        _lastMouseMoveSentAtMs = nowMs;

        if (!TryMapSurfacePointToRemote(surface, e.GetPosition(surface), out var x, out var y))
        {
            return;
        }

        // De-dup noisy mouse move events.
        if (_lastSentMouseX == x && _lastSentMouseY == y)
        {
            return;
        }
        _lastSentMouseX = x;
        _lastSentMouseY = y;

        _ = SafeSendMouseAsync(new MouseInputPayload { Action = "move", X = x, Y = y });
    }

    public void HandleRemoteSurfaceMouseDown(FrameworkElement surface, MouseButtonEventArgs e)
    {
        if (_role != ConnectionRole.Viewer)
        {
            return;
        }
        if (_dataChannelCoordinator is null)
        {
            return;
        }

        if (!TryMapSurfacePointToRemote(surface, e.GetPosition(surface), out var x, out var y))
        {
            return;
        }

        var button = e.ChangedButton switch
        {
            MouseButton.Right => 2,
            MouseButton.Middle => 3,
            _ => 1
        };

        _ = SafeSendMouseAsync(new MouseInputPayload { Action = "move", X = x, Y = y });
        _ = SafeSendMouseAsync(new MouseInputPayload { Action = "down", Button = button, X = x, Y = y });
    }

    public void HandleRemoteSurfaceMouseUp(FrameworkElement surface, MouseButtonEventArgs e)
    {
        if (_role != ConnectionRole.Viewer)
        {
            return;
        }
        if (_dataChannelCoordinator is null)
        {
            return;
        }

        if (!TryMapSurfacePointToRemote(surface, e.GetPosition(surface), out var x, out var y))
        {
            return;
        }

        var button = e.ChangedButton switch
        {
            MouseButton.Right => 2,
            MouseButton.Middle => 3,
            _ => 1
        };

        _ = SafeSendMouseAsync(new MouseInputPayload { Action = "move", X = x, Y = y });
        _ = SafeSendMouseAsync(new MouseInputPayload { Action = "up", Button = button, X = x, Y = y });
    }

    public void HandleRemoteSurfaceMouseWheel(FrameworkElement surface, MouseWheelEventArgs e)
    {
        if (_role != ConnectionRole.Viewer)
        {
            return;
        }
        if (_dataChannelCoordinator is null)
        {
            return;
        }

        if (!TryMapSurfacePointToRemote(surface, e.GetPosition(surface), out var x, out var y))
        {
            return;
        }

        _ = SafeSendMouseAsync(new MouseInputPayload { Action = "wheel", X = x, Y = y, Delta = e.Delta });
    }

    public void HandleRemoteSurfaceKeyDown(KeyEventArgs e)
    {
        if (_role != ConnectionRole.Viewer)
        {
            return;
        }
        if (_dataChannelCoordinator is null)
        {
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (vk == 0)
        {
            return;
        }

        _ = SafeSendKeyboardAsync(new KeyboardInputPayload
        {
            Action = "down",
            VirtualKey = vk,
            Alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0,
            Ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0,
            Shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
        });

        // Prevent local UI from handling e.g. Tab navigation.
        e.Handled = true;
    }

    public void HandleRemoteSurfaceKeyUp(KeyEventArgs e)
    {
        if (_role != ConnectionRole.Viewer)
        {
            return;
        }
        if (_dataChannelCoordinator is null)
        {
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (vk == 0)
        {
            return;
        }

        _ = SafeSendKeyboardAsync(new KeyboardInputPayload
        {
            Action = "up",
            VirtualKey = vk,
            Alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0,
            Ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0,
            Shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
        });
        e.Handled = true;
    }

    public void HandleRemoteSurfaceLostFocus()
    {
        _lastSentMouseX = null;
        _lastSentMouseY = null;
    }

    private bool TryMapSurfacePointToRemote(FrameworkElement surface, Point p, out int x, out int y)
    {
        x = 0;
        y = 0;

        if (_remoteFrameBitmap is null)
        {
            return false;
        }

        // Fallback: if we didn't receive meta yet, map to (0..frame) without offsets.
        var meta = _remoteScreenMeta;
        var captureX = meta?.CaptureX ?? 0;
        var captureY = meta?.CaptureY ?? 0;
        var captureW = meta?.Width ?? _remoteFrameBitmap.PixelWidth;
        var captureH = meta?.Height ?? _remoteFrameBitmap.PixelHeight;
        if (captureW <= 0 || captureH <= 0)
        {
            return false;
        }

        // The Image uses Stretch=Uniform. We map from surface coordinates to the displayed image rect,
        // then scale into capture rect.
        var surfaceW = surface.ActualWidth;
        var surfaceH = surface.ActualHeight;
        if (surfaceW <= 1 || surfaceH <= 1)
        {
            return false;
        }

        var frameW = (double)_remoteFrameBitmap.PixelWidth;
        var frameH = (double)_remoteFrameBitmap.PixelHeight;
        if (frameW <= 1 || frameH <= 1)
        {
            return false;
        }

        var scale = Math.Min(surfaceW / frameW, surfaceH / frameH);
        var displayW = frameW * scale;
        var displayH = frameH * scale;
        var offsetX = (surfaceW - displayW) / 2;
        var offsetY = (surfaceH - displayH) / 2;

        var rx = (p.X - offsetX) / scale;
        var ry = (p.Y - offsetY) / scale;
        if (rx < 0 || ry < 0 || rx >= frameW || ry >= frameH)
        {
            return false;
        }

        // Map frame pixel -> capture pixel, then add absolute screen offset.
        var sx = (int)Math.Round(rx * captureW / frameW);
        var sy = (int)Math.Round(ry * captureH / frameH);

        // Clamp (avoids SetCursorPos outside capture bounds due to rounding).
        sx = Math.Clamp(sx, 0, captureW - 1);
        sy = Math.Clamp(sy, 0, captureH - 1);

        x = captureX + sx;
        y = captureY + sy;
        return true;
    }

    private Task SafeSendMouseAsync(MouseInputPayload payload)
    {
        var dc = _dataChannelCoordinator;
        if (dc is null)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                await dc.SendMouseAsync(payload, _lifetimeCts.Token);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logService.Debug("DataChannel", "send_mouse_failed_" + ex.Message);
            }
        });
    }

    private Task SafeSendKeyboardAsync(KeyboardInputPayload payload)
    {
        var dc = _dataChannelCoordinator;
        if (dc is null)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                await dc.SendKeyboardAsync(payload, _lifetimeCts.Token);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logService.Debug("DataChannel", "send_keyboard_failed_" + ex.Message);
            }
        });
    }
}

internal sealed class IceAttemptOptions
{
    public IceAttemptOptions(string name, int timeoutMs, bool disableStun, bool disableTurn, bool preferRelay, string expectedRoute)
    {
        Name = name;
        TimeoutMs = timeoutMs;
        DisableStun = disableStun;
        DisableTurn = disableTurn;
        PreferRelay = preferRelay;
        ExpectedRoute = expectedRoute;
    }

    public string Name { get; }
    public int TimeoutMs { get; }
    public bool DisableStun { get; }
    public bool DisableTurn { get; }
    public bool PreferRelay { get; }
    public string ExpectedRoute { get; }
}

internal sealed class IncomingFileTransferState : IDisposable
{
    public IncomingFileTransferState(string transferId, string fileName, string targetPath, string expectedHash, long expectedSize, FileStream stream)
    {
        TransferId = transferId;
        FileName = fileName;
        TargetPath = targetPath;
        ExpectedHash = expectedHash;
        ExpectedSize = expectedSize;
        Stream = stream;
    }

    public string TransferId { get; }
    public string FileName { get; }
    public string TargetPath { get; }
    public string ExpectedHash { get; }
    public long ExpectedSize { get; }
    public FileStream Stream { get; }
    public int NextSequence { get; set; }
    public long BytesWritten { get; set; }
    public object SyncRoot { get; } = new();

    public void Dispose()
    {
        try
        {
            Stream.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private bool _isBusy;

    public AsyncRelayCommand(Func<Task> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isBusy;

    public async void Execute(object? parameter)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        finally
        {
            _isBusy = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
