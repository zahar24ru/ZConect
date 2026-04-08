using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FileTransfer;
using SessionClient;
using UiApp.Models;
using WebRtcTransport;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel
{
    private async Task CreateSessionAsync()
    {
        if (_role == ConnectionRole.Host && _signalingClient is not null)
        {
            StatusText = "Сессия уже создана";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        SetStatus("Создание сессии...", ConnectionState.Connecting);
        _logService.Info("UiApp", "create_session_clicked");

        var requestedTtlSec = _settings.UnattendedExpiresInSec > 0 ? _settings.UnattendedExpiresInSec : (int?)null;
        var machineId = !string.IsNullOrWhiteSpace(_settings.MachineId) ? _settings.MachineId : null;
        var response = await _sessionApiClient.CreateSessionAsync(ServerApiBaseUrl, AllowUnattended, requestedTtlSec, machineId);
        if (response is null)
        {
            SetStatus("Ошибка создания сессии", ConnectionState.Error);
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

        _role = ConnectionRole.Host;
        _fileTransferOnly = FileTransferOnly;
        await ConnectWsAsync(wsUrl, wsToken, sessionId, startAsCaller: false, role: _role, fileTransferOnly: FileTransferOnly);

        // Persist session info so the app can reuse it after restart.
        _settings.LastSessionId = sessionId;
        _settings.LastLoginCode = loginCode;
        _settings.LastPassCode = passCode;
        _settings.LastSessionExpiresAtUtcTicks = DateTime.UtcNow.AddSeconds(expiresInSec).Ticks;
        _settingsService.Save(_settings);

        StartSessionCountdown(expiresInSec);

        _uiContext.Post(_ =>
        {
            SetStatus("Сессия создана, ожидаем подключение", ConnectionState.Connecting);
        }, null);
        _logService.Info("UiApp", "create_session_success");
    }

    private async Task RefreshSessionAsync()
    {
        if (string.IsNullOrEmpty(_currentSessionId))
        {
            StatusText = "Нет активной сессии для обновления";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        _logService.Info("UiApp", "refresh_session_clicked");
        var requestedTtl = _settings.UnattendedExpiresInSec > 0 ? _settings.UnattendedExpiresInSec : (int?)null;
        var response = await _sessionApiClient.RefreshSessionAsync(ServerApiBaseUrl, _currentSessionId, requestedTtl);
        if (response is null)
        {
            StatusText = "Ошибка обновления пароля сессии";
            OnPropertyChanged(nameof(StatusText));
            _logService.Warn("UiApp", "refresh_session_failed");
            return;
        }

        // Persist updated credentials.
        _settings.LastPassCode = response.PassCode;
        _settings.LastSessionExpiresAtUtcTicks = DateTime.UtcNow.AddSeconds(response.ExpiresInSec).Ticks;
        _settingsService.Save(_settings);

        StartSessionCountdown(response.ExpiresInSec);

        _uiContext.Post(_ =>
        {
            LoginCode = response.LoginCode;
            PassCode = response.PassCode;
            OnPropertyChanged(nameof(LoginCode));
            OnPropertyChanged(nameof(PassCode));
            SetStatus("Пароль обновлён", ConnectionState.Connecting);
        }, null);
        _logService.Info("UiApp", "refresh_session_success");
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

        if (JoinLoginCode.Length != 8 || JoinPassCode.Length != 8)
        {
            StatusText = "Логин и пароль должны быть по 8 цифр";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        _logService.Info("UiApp", "join_session_clicked");
        await ConnectAsViewerAsync(ServerApiBaseUrl, JoinLoginCode, JoinPassCode, displayName: null);
    }

    /// <summary>
    /// Единая точка входа для viewer-подключения.
    /// Используется и из вкладки «Подключиться», и из адресной книги.
    /// </summary>
    private async Task ConnectAsViewerAsync(string serverBaseUrl, string loginCode, string passCode, string? displayName)
    {
        SetStatus("Подключение к сессии...", ConnectionState.Connecting);

        var response = await _sessionApiClient.JoinSessionAsync(serverBaseUrl, loginCode, passCode);
        if (response is null)
        {
            SetStatus("Ошибка подключения", ConnectionState.Error);
            _logService.Warn("UiApp", "join_session_failed");
            return;
        }

        var wsUrl = ResolveWsUrl(serverBaseUrl, response.WsUrl);
        _role = ConnectionRole.Viewer;
        _fileTransferOnly = FileTransferOnly;
        await ConnectWsWithAutoIcePriorityAsync(wsUrl, response.WsToken, response.SessionId, role: _role, fileTransferOnly: FileTransferOnly);
        if (!_fileTransferOnly)
        {
            _uiContext.Post(_ => RemoteScreenWindowRequested?.Invoke(), null);
        }
        _uiContext.Post(_ =>
        {
            OnPropertyChanged(nameof(IsViewerAndConnected));
            OnPropertyChanged(nameof(RemoteDesktopButtonVisibility));
        }, null);

        var label = displayName ?? response.SessionId;
        SetStatus($"Подключено: {label}", ConnectionState.Connected);
        _logService.Info("UiApp", "join_success name=" + label);
    }

    private static string ResolveWsUrl(string serverApiBaseUrl, string wsUrlFromResponse)
    {
        if (string.IsNullOrWhiteSpace(wsUrlFromResponse))
            wsUrlFromResponse = "/ws";
        if (wsUrlFromResponse.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || wsUrlFromResponse.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return wsUrlFromResponse;
        try
        {
            var baseUri = new Uri(serverApiBaseUrl.TrimEnd('/'));
            var scheme = baseUri.Scheme == "https" ? "wss" : "ws";
            var authority = baseUri.GetComponents(UriComponents.Host | UriComponents.Port, UriFormat.Unescaped);
            var path = wsUrlFromResponse.StartsWith("/") ? wsUrlFromResponse : "/" + wsUrlFromResponse;
            return $"{scheme}://{authority}{path}";
        }
        catch
        {
            return wsUrlFromResponse;
        }
    }

    /// <summary>
    /// "Connect Fast, Upgrade Later" algorithm (like RustDesk/Tailscale).
    ///
    /// Instead of sequentially testing LAN → STUN → TURN (slow, prone to false positives),
    /// we use ALL ICE methods simultaneously and wait for REAL connectivity proof
    /// (data channel opens = SCTP handshake completed = bidirectional transport works).
    ///
    /// Phase 1: Connect as fast as possible with all methods enabled.
    /// Phase 2: Once connected, the ICE upgrade monitor optimizes the path in background.
    ///
    /// Why this is better:
    /// - WebRTC's ICE agent is designed to test all candidate pairs in parallel and pick the best.
    /// - Sequential filtering fights against ICE's design and causes false positives
    ///   (e.g. host candidates "match" but can't connect across cities).
    /// - Typical connect time: 1-3s (vs 12-60s with sequential approach on high-latency servers).
    /// </summary>
    private async Task ConnectWsWithAutoIcePriorityAsync(string wsUrlFromServer, string wsToken, string sessionId, ConnectionRole role, bool fileTransferOnly = false)
    {
        // Phase 1: Establish WS + WebRTC with ALL ICE methods enabled.
        // No filtering — let the ICE agent do its job with all candidate types.
        await ConnectWsAsync(wsUrlFromServer, wsToken, sessionId, startAsCaller: false, role: role, fileTransferOnly: fileTransferOnly);

        var wsRtt = _signalingClient?.HandshakeRttMs ?? -1;
        _logService.Info("WebRTC", $"auto_ice_connect_fast_rtt_{wsRtt}ms");

        SetStatus("Подключение (ICE)...", ConnectionState.Connecting);

        // Start signaling as caller with ALL candidates (no filters).
        if (_signalingCoordinator is not null)
        {
            _signalingCoordinator.SetIceCandidateFilters(null, null);
            await _signalingCoordinator.StartAsCallerAsync(sessionId, _lifetimeCts.Token);
        }

        // Wait for REAL connectivity: data channel must actually open.
        // This is the definitive proof that SCTP/DTLS handshake completed
        // and bidirectional transport works — not just "candidates were exchanged".
        var connectTimeoutMs = Math.Max(15_000, wsRtt > 0 ? (int)(wsRtt * 8) : 15_000);
        connectTimeoutMs = Math.Min(connectTimeoutMs, 30_000); // cap at 30s
        _logService.Info("WebRTC", $"auto_ice_waiting_data_channel_timeout_{connectTimeoutMs}ms");

        var connected = await WaitForDataChannelOpenAsync(connectTimeoutMs, _lifetimeCts.Token);

        if (connected)
        {
            var route = InferRouteType(_localIceCandidateType, _remoteIceCandidateType);
            var label = IceRouteDisplayLabel(route);
            _logService.Info("WebRTC", $"auto_ice_connected_via_{route}");
            SetStatus($"Подключено: {label}", ConnectionState.Connected);

            // Start init messages, clipboard, cursor loops.
            _ = Task.Run(() => SendInitDataChannelMessagesWithRetryAsync(role, _lifetimeCts.Token), _lifetimeCts.Token);
            StartClipboardSyncLoopIfNeeded();
            StartCursorSyncLoopIfNeeded();
        }
        else
        {
            // Data channel didn't open — connection failed.
            // Last resort: keep the peer alive and start init loop anyway
            // (SendInitDataChannelMessagesWithRetryAsync has its own 30s retry).
            _logService.Warn("WebRTC", "auto_ice_data_channel_timeout_starting_retry_loop");
            SetStatus("ICE: ожидание соединения...", ConnectionState.Connecting);
            _ = Task.Run(() => SendInitDataChannelMessagesWithRetryAsync(role, _lifetimeCts.Token), _lifetimeCts.Token);
            StartClipboardSyncLoopIfNeeded();
            StartCursorSyncLoopIfNeeded();
        }

        // Phase 2: Background ICE upgrade monitor.
        // If connected via relay, periodically tries ICE restart to find a more direct path.
        // If already on optimal path, still monitors for network changes.
        StartIceUpgradeMonitor(sessionId, _lifetimeCts.Token);
    }

    /// <summary>
    /// Wait for a data channel to actually open — the definitive proof of real connectivity.
    /// Returns true if any data channel opens within the timeout.
    /// </summary>
    private async Task<bool> WaitForDataChannelOpenAsync(int timeoutMs, CancellationToken ct)
    {
        var dc = _dataChannelCoordinator;
        if (dc is null) return false;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpen(DataChannelKind _) => tcs.TrySetResult(true);
        dc.SubscribeChannelOpened(OnOpen);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            // Also check first video frame as secondary signal.
            var pollTask = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (_firstRemoteFrameTcs?.Task.IsCompletedSuccessfully == true)
                    {
                        tcs.TrySetResult(true);
                        return;
                    }
                    await Task.Delay(200, cts.Token);
                }
            }, cts.Token);

            try
            {
                await tcs.Task.WaitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false; // timeout
            }
        }
        finally
        {
            dc.UnsubscribeChannelOpened(OnOpen);
        }
    }

    /// <summary>Wire up all DataChannelCoordinator event subscriptions + FileTransferService. Reusable across connect/reset paths.</summary>
    private void WireUpDataChannelCoordinator(ConnectionRole role)
    {
        var dc = _dataChannelCoordinator!;
        if (role == ConnectionRole.Host)
        {
            dc.MouseReceived += OnMouseInputReceived;
            dc.KeyboardReceived += OnKeyboardInputReceived;
            dc.CtrlAltDelReceived += OnCtrlAltDelReceived;
        }
        dc.ScreenMetaReceived += payload =>
        {
            _remoteScreenMeta = payload;
            _logService.Info("RemoteScreen", $"screen_meta_received_{payload.CaptureX}_{payload.CaptureY}_{payload.Width}x{payload.Height}_{payload.DisplayId}");
        };
        dc.HostVideoSettingsRequestReceived += payload =>
        {
            _ = Task.Run(async () =>
            {
                try { await ApplyHostVideoSettingsRequestAsync(payload); }
                catch (Exception ex) { _logService.Warn("RemoteScreen", "apply_video_settings_exception_" + ex.Message); }
            });
        };
        dc.HostDisplaysReceived += payload => OnHostDisplaysReceived(payload);
        dc.HostDisplaysRequestReceived += _payload =>
        {
            _ = Task.Run(async () => { try { await SendHostDisplaysIfHostAsync(_lifetimeCts.Token); } catch { } });
        };
        dc.CursorShapeReceived += payload => OnCursorShapeReceived(payload);
        dc.ClipboardReceived += payload => OnClipboardReceived(payload);
        dc.PingReceived += OnPingReceived;
        dc.PongReceived += OnPongReceived;

        // File transfer
        var fileChannel = new DataChannelFileTransferChannel(dc);
        var defaultSaveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ZConectReceived");
        _incomingSaveDirHolder = new IncomingSaveDirHolder { Path = defaultSaveDir };
        _outgoingTargetDirHolder = new OutgoingTargetDirHolder();
        _incomingSaveDirPath = defaultSaveDir;
        _fileTransferService = new FileTransferService(fileChannel,
            getIncomingSaveDir: () => !string.IsNullOrEmpty(_incomingSaveDirPath) ? _incomingSaveDirPath : defaultSaveDir,
            getOutgoingTargetDir: () => _outgoingTargetDirHolder?.Path?.Trim() ?? string.Empty,
            onLog: (cat, ev, args) =>
                _logService.Info(cat, args.Length > 0 ? ev + " " + string.Join(" ", args) : ev));
        _fileTransferService.TransferProgress += item =>
        {
            _uiContext.Post(_ =>
            {
                var dir = item.Direction == TransferDirection.Outgoing ? "Отправка" : "Прием";
                FileTransferProgressText = item.TotalBytes > 0
                    ? $"{dir}: {item.FileName} ({item.ProgressPercent}%)"
                    : $"{dir}: {item.FileName} ({item.CurrentBytes} bytes)";
                if (item.Status == TransferStatus.Completed)
                    StatusText = $"Файл {(item.Direction == TransferDirection.Outgoing ? "отправлен" : "получен")}: {item.FileName}";
                else if (item.Status == TransferStatus.Failed && !string.IsNullOrEmpty(item.ErrorMessage))
                    StatusText = $"Ошибка: {item.ErrorMessage}";
                OnPropertyChanged(nameof(FileTransferProgressText));
                OnPropertyChanged(nameof(StatusText));
            }, null);
        };
        _fileTransferService.TransferCompleted += item =>
        {
            _uiContext.Post(_ => { OnPropertyChanged(nameof(StatusText)); }, null);
        };
        _fileTransferService.TransferFailed += item =>
        {
            _uiContext.Post(_ =>
            {
                StatusText = $"Передача файла: {item.ErrorMessage ?? "ошибка"}";
                OnPropertyChanged(nameof(StatusText));
            }, null);
        };

        // Remote directory protocol
        dc.DirListRequestReceived += OnDirListRequestReceived;
        dc.DirListResponseReceived += p =>
        {
            var match = p.RequestId == _pendingDirListRequestId;
            _logService.Info("FT", "dir_list_response_received path=" + (p.Path ?? "") + " reqId=" + p.RequestId + " pending=" + (_pendingDirListRequestId ?? "null") + " accepted=" + match);
            if (match)
            {
                _pendingDirListRequestId = null;
                RemoteDirListReceived?.Invoke(p);
            }
        };
        dc.FileRequestReceived += OnFileRequestReceived;
        dc.CreateFolderRequestReceived += OnCreateFolderRequestReceived;
        dc.CreateFolderResponseReceived += p =>
        {
            if (p.RequestId == _pendingCreateFolderRequestId)
            {
                _pendingCreateFolderRequestId = null;
                CreateFolderResponseReceived?.Invoke(p);
            }
        };
        dc.DeleteRequestReceived += OnDeleteRequestReceived;
        dc.DeleteResponseReceived += p =>
        {
            if (p.RequestId == _pendingDeleteRequestId)
            {
                _pendingDeleteRequestId = null;
                DeleteResponseReceived?.Invoke(p);
            }
        };
    }

    private async Task ConnectWsAsync(string wsUrlFromServer, string wsToken, string sessionId, bool startAsCaller, ConnectionRole role, bool fileTransferOnly = false)
    {
        // Ensure previous connection is fully cleaned up before starting a new one.
        await CleanupConnectionAsync().ConfigureAwait(false);
        _lifetimeCts = new CancellationTokenSource();

        _role = role;
        _fileTransferOnly = fileTransferOnly;

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
        _currentWsUrl = wsUrl;
        _currentWsToken = wsToken;

        _signalingClient = new WebSocketSignalingClient();
        _signalingClient.MessageReceived += OnSignalingMessageReceived;
        _signalingClient.Disconnected += OnSignalingDisconnected;
        IPeerConnectionAgent peerAgent;
        IDataChannelAgent dataAgent;
        try
        {
            _realPeerAgent = new MixedRealityPeerConnectionAgent(new TransportSettings
            {
                StunUrl = StunUrl,
                TurnUrl = TurnUrl,
                TurnUsername = TurnUsername,
                TurnPassword = TurnPassword,
                PreferRelay = false,
                PreferLanVpnNoTurn = false
            }, msg => _logService.Debug("WebRTC", msg));
            var realDataAgent = new MixedRealityDataChannelAgent();
            _realPeerAgent.DataChannelAdded += ch => realDataAgent.AttachChannel(ch);
            await _realPeerAgent.InitializeAsync(_lifetimeCts.Token, includeVideoTransceiver: !fileTransferOnly);

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
            beforeCreateAnswerAsync: async (offerSdp) =>
            {
                // Включаем видео на host только если во входящем offer есть video (иначе режим «только файлы» или renegotiation без видео).
                var offerHasVideo = offerSdp.Contains("m=video", StringComparison.OrdinalIgnoreCase);
                if (_role == ConnectionRole.Host && !_hostVideoStarted && _realPeerAgent is not null && offerHasVideo)
                {
                    _hostVideoStarted = true;
                    await ConfigureLocalVideoAsync(_realPeerAgent);
                    StartAutoQualityLoopIfNeeded();
                    StartRttLoopIfNeeded();
                    _logService.Info("ScreenCapture", "local_video_started_on_offer");
                }
            },
            shouldSendLocalIceCandidate: null,
            shouldAcceptRemoteIceCandidate: null);
        _signalingCoordinator.IceCandidateObserved += OnIceCandidateObserved;
        _signalingCoordinator.SetSession(sessionId);
        _isDataChannelReal = dataAgent is not MockDataChannelAgent;
        _dataChannelCoordinator = new DataChannelCoordinator(
            dataAgent,
            msg => _logService.Debug("DataChannel", msg));
        WireUpDataChannelCoordinator(role);

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
            SetStatus("WS подключение не удалось", ConnectionState.Error);
            return;
        }

        // Init messages over data channels: SCTP/DataChannel может открыться чуть позже, чем WS и SDP/ICE.
        _ = Task.Run(() => SendInitDataChannelMessagesWithRetryAsync(role, _lifetimeCts.Token), _lifetimeCts.Token);
        StartClipboardSyncLoopIfNeeded();
        StartCursorSyncLoopIfNeeded();
    }

    /// <summary>Вызывается при закрытии окна удалённого экрана зрителем — отключает текущее соединение.</summary>
    public void DisconnectViewerAsync()
    {
        if (_role != ConnectionRole.Viewer) return;
        _logService.Info("UiApp", "viewer_disconnect_requested");
        _ = Task.Run(async () =>
        {
            await CleanupConnectionAsync().ConfigureAwait(false);
            _uiContext.Post(_ => SetStatus("Отключён", ConnectionState.Idle), null);
        });
    }

    private async Task CleanupConnectionAsync()
    {
        StopSessionCountdown();
        _uiContext.Post(_ =>
        {
            ViewerConnectionStatus = string.Empty;
            ViewerConnectionStatusBrush = Brushes.Transparent;
        }, null);

        // Release any stuck keys on the remote host before tearing down.
        try { ReleaseAllPressedKeys(); } catch { /* ignore */ }

        // Stop ICE upgrade monitor (unsubscribe network events).
        try { StopIceUpgradeMonitor(); } catch { /* ignore */ }

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
        _isDataChannelReal = false;
        _pendingDirListRequestId = null;
        _pendingCreateFolderRequestId = null;
        _pendingDeleteRequestId = null;
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
        Interlocked.Exchange(ref _rttLoopStarted, 0);
        Interlocked.Exchange(ref _autoQualityLoopStarted, 0);
        Interlocked.Exchange(ref _autoRefreshInProgress, 0);
        Interlocked.Exchange(ref _wsReconnecting, 0);
        _lastObservedClipboardText = string.Empty;
        _lastSentClipboardText = string.Empty;
        _lastAppliedRemoteClipboardText = string.Empty;
        _lastAppliedRemoteClipboardAtUtc = DateTime.MinValue;
        _lastSentCursorType = string.Empty;
        ResetVideoStats();
        _uiContext.Post(_ =>
        {
            RemoteCursor = Cursors.Arrow;
            ViewerAvailableDisplayIds.Clear();
            RefreshViewerDisplayIdsFallback();
            OnPropertyChanged(nameof(IsViewerAndConnected));
            OnPropertyChanged(nameof(RemoteDesktopButtonVisibility));
            ConnectionState = ConnectionState.Idle;
        }, null);
        _fileTransferService?.CancelAll();

        try
        {
            _lifetimeCts.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private void ClearSavedSession()
    {
        _settings.LastSessionId = string.Empty;
        _settings.LastLoginCode = string.Empty;
        _settings.LastPassCode = string.Empty;
        _settings.LastSessionExpiresAtUtcTicks = 0;
        _settingsService.Save(_settings);
    }

    private async Task TryCloseCurrentSessionAsync()
    {
        var sessionId = _currentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        // Clear persisted session data since we're explicitly closing.
        ClearSavedSession();

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
        if (dc is null) return;

        var shouldSendScreenMeta = role == ConnectionRole.Host;
        var shouldSendHostDisplays = role == ConnectionRole.Host;
        var shouldRequestHostDisplays = role == ConnectionRole.Viewer;
        var shouldSendClipboardInit = true;

        // Wait for any data channel to open (event-driven) with a 30s hard timeout.
        var channelOpenTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChannelOpen(DataChannelKind _) => channelOpenTcs.TrySetResult();
        dc.SubscribeChannelOpened(OnChannelOpen);

        // Try immediately first (channel may already be open), then wait for event.
        for (var attempt = 1; attempt <= 60; attempt++)
        {
            if (ct.IsCancellationRequested) return;

            var allOk = true;
            try
            {
                if (shouldSendScreenMeta && _localScreenMeta is not null)
                { await dc.SendScreenMetaAsync(_localScreenMeta, ct); shouldSendScreenMeta = false; }
            }
            catch { allOk = false; }

            try
            {
                if (shouldSendHostDisplays)
                { await SendHostDisplaysIfHostAsync(ct); shouldSendHostDisplays = false; }
            }
            catch { allOk = false; }

            try
            {
                if (shouldRequestHostDisplays)
                {
                    await dc.SendHostDisplaysRequestAsync(new HostDisplaysRequestPayload
                    { RequestId = Guid.NewGuid().ToString("N") }, ct);
                    shouldRequestHostDisplays = false;
                }
            }
            catch { allOk = false; }

            try
            {
                if (shouldSendClipboardInit)
                {
                    await dc.SendClipboardAsync(new ClipboardTextPayload
                    { Text = "zconect-init", OriginPeerId = "local" }, ct);
                    shouldSendClipboardInit = false;
                }
            }
            catch { allOk = false; }

            if (allOk || (!shouldSendScreenMeta && !shouldSendHostDisplays && !shouldRequestHostDisplays && !shouldSendClipboardInit))
            {
                StartRttLoopIfNeeded();
                return;
            }

            // Wait for channel open event (up to 1s) instead of blind 500ms polling.
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(1000);
                await channelOpenTcs.Task.WaitAsync(cts.Token);
                // Channel opened — retry sends immediately.
                _logService.Debug("DataChannel", $"channel_opened_event_at_attempt_{attempt}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 1s timeout, continue polling.
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Повторно запросить у хоста список экранов (если при подключении список не пришёл).</summary>
    private async Task RequestHostDisplaysAsync()
    {
        if (_role != ConnectionRole.Viewer || _dataChannelCoordinator is null)
        {
            return;
        }
        try
        {
            await _dataChannelCoordinator.SendHostDisplaysRequestAsync(new HostDisplaysRequestPayload
            {
                RequestId = Guid.NewGuid().ToString("N")
            }, _lifetimeCts.Token);
            _logService.Debug("DataChannel", "host_displays_requested_manual");
        }
        catch (Exception ex)
        {
            _logService.Debug("DataChannel", "host_displays_request_manual_failed_" + ex.Message);
        }
    }

    /// <summary>
    /// Host-only: dispose WebRTC peer/data channel but keep the WS connection alive,
    /// then re-create WebRTC so the host can accept a new viewer on the same session.
    /// </summary>
    private async Task ResetForNewViewerAsync()
    {
        _logService.Info("UiApp", "host_reset_for_new_viewer");

        // 1. Cancel background tasks (clipboard, cursor, etc.)
        try { _lifetimeCts.Cancel(); } catch { }

        // 2. Unsubscribe and dispose old WebRTC objects.
        try { _signalingCoordinator?.Detach(); } catch { }
        try { if (_signalingCoordinator is not null) _signalingCoordinator.IceCandidateObserved -= OnIceCandidateObserved; } catch { }
        try
        {
            if (_realPeerAgent is not null)
                _realPeerAgent.RemoteVideoFrameReceived -= OnRemoteVideoFrameReceived;
        }
        catch { }
        try { _realPeerAgent?.Dispose(); } catch { }
        _fileTransferService?.CancelAll();
        _dataChannelCoordinator = null;
        _signalingCoordinator = null;
        _realPeerAgent = null;
        _isDataChannelReal = false;

        // 3. Reset state flags.
        try { _lifetimeCts.Dispose(); } catch { }
        _lifetimeCts = new CancellationTokenSource();
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
        Interlocked.Exchange(ref _clipboardLoopStarted, 0);
        Interlocked.Exchange(ref _cursorLoopStarted, 0);
        Interlocked.Exchange(ref _rttLoopStarted, 0);
        Interlocked.Exchange(ref _autoQualityLoopStarted, 0);
        Interlocked.Exchange(ref _autoRefreshInProgress, 0);
        _lastObservedClipboardText = string.Empty;
        _lastSentClipboardText = string.Empty;
        _lastAppliedRemoteClipboardText = string.Empty;
        _lastAppliedRemoteClipboardAtUtc = DateTime.MinValue;
        _lastSentCursorType = string.Empty;
        _pendingDirListRequestId = null;
        _pendingCreateFolderRequestId = null;
        _pendingDeleteRequestId = null;
        _uiContext.Post(_ =>
        {
            RemoteCursor = Cursors.Arrow;
            ViewerAvailableDisplayIds.Clear();
            RefreshViewerDisplayIdsFallback();
            OnPropertyChanged(nameof(IsViewerAndConnected));
            OnPropertyChanged(nameof(RemoteDesktopButtonVisibility));
        }, null);

        // 4. Re-create WebRTC peer agent.
        IPeerConnectionAgent peerAgent;
        IDataChannelAgent dataAgent;
        try
        {
            _realPeerAgent = new MixedRealityPeerConnectionAgent(new TransportSettings
            {
                StunUrl = StunUrl,
                TurnUrl = TurnUrl,
                TurnUsername = TurnUsername,
                TurnPassword = TurnPassword,
                PreferRelay = false,
                PreferLanVpnNoTurn = false
            }, msg => _logService.Debug("WebRTC", msg));
            var realDataAgent = new MixedRealityDataChannelAgent();
            _realPeerAgent.DataChannelAdded += ch => realDataAgent.AttachChannel(ch);
            await _realPeerAgent.InitializeAsync(_lifetimeCts.Token, includeVideoTransceiver: !_fileTransferOnly);
            peerAgent = _realPeerAgent;
            dataAgent = realDataAgent;
        }
        catch (Exception ex)
        {
            peerAgent = new MockPeerConnectionAgent();
            await peerAgent.InitializeAsync();
            dataAgent = new MockDataChannelAgent();
            _logService.Warn("UiApp", "reset_webrtc_fallback_mock_" + ex.Message);
        }
        peerAgent.RemoteVideoFrameReceived += OnRemoteVideoFrameReceived;

        // 5. Re-create SignalingCoordinator (reuses existing _signalingClient).
        _signalingCoordinator = new SignalingCoordinator(
            _signalingClient!,
            peerAgent,
            m => _logService.Debug("Signaling", m),
            beforeCreateAnswerAsync: async (offerSdp) =>
            {
                var offerHasVideo = offerSdp.Contains("m=video", StringComparison.OrdinalIgnoreCase);
                if (_role == ConnectionRole.Host && !_hostVideoStarted && _realPeerAgent is not null && offerHasVideo)
                {
                    _hostVideoStarted = true;
                    await ConfigureLocalVideoAsync(_realPeerAgent);
                    StartAutoQualityLoopIfNeeded();
                    StartRttLoopIfNeeded();
                    _logService.Info("ScreenCapture", "local_video_started_on_offer_reset");
                }
            });
        _signalingCoordinator.IceCandidateObserved += OnIceCandidateObserved;
        _signalingCoordinator.SetSession(_currentSessionId);

        // 6. Re-create DataChannelCoordinator.
        _isDataChannelReal = dataAgent is not MockDataChannelAgent;
        _dataChannelCoordinator = new DataChannelCoordinator(
            dataAgent,
            m => _logService.Debug("DataChannel", m));
        WireUpDataChannelCoordinator(ConnectionRole.Host);

        // 7. Re-announce host state so new viewer can start.
        try
        {
            await _signalingClient!.SendAsync("peer_state", _currentSessionId, new { state = "host_ready" }, _lifetimeCts.Token);
            _logService.Info("Signaling", "host_reannounced_after_reset");
        }
        catch (Exception ex)
        {
            _logService.Warn("Signaling", "host_reannounce_failed_" + ex.Message);
        }

        _ = Task.Run(() => SendInitDataChannelMessagesWithRetryAsync(ConnectionRole.Host, _lifetimeCts.Token), _lifetimeCts.Token);
        StartClipboardSyncLoopIfNeeded();
        StartCursorSyncLoopIfNeeded();
        _logService.Info("UiApp", "host_ready_for_new_viewer");
    }

    private void OnSignalingMessageReceived(SignalingMessage msg)
    {
        // Skip high-frequency ICE messages — already logged by SignalingCoordinator
        if (msg.Type is not "ice")
            _logService.Debug("UiApp", $"ws_message_{msg.Type}");

        if (msg.Type == "peer_disconnected")
        {
            _logService.Warn("UiApp", "peer_disconnected_received role=" + _role);
            if (_role == ConnectionRole.Host)
            {
                // Host stays connected to WS, resets WebRTC to accept new viewer.
                _ = Task.Run(async () =>
                {
                    await ResetForNewViewerAsync().ConfigureAwait(false);
                    _uiContext.Post(_ =>
                    {
                        SetStatus("Viewer отключился. Ожидаем повторное подключение...", ConnectionState.Connecting);
                        ViewerConnectionStatus = "Зритель отключился";
                        ViewerConnectionStatusBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                    }, null);
                });
            }
            else if (_role == ConnectionRole.Viewer)
            {
                // Viewer: try to reconnect WS (host may have temporarily lost connection).
                _uiContext.Post(_ => SetStatus("Хост отключился, переподключение...", ConnectionState.Connecting), null);
                _ = Task.Run(async () =>
                {
                    try { await WsReconnectLoopAsync(); }
                    catch { /* ignore */ }
                });
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    await CleanupConnectionAsync().ConfigureAwait(false);
                    _uiContext.Post(_ => SetStatus("Удалённая сторона отключилась", ConnectionState.Idle), null);
                });
            }
            return;
        }

        if (msg.Type == "peer_state")
        {
            // Host may have connected before viewer; re-announce host state when we notice the other side joined.
            if (_role == ConnectionRole.Host && !_hostPeerStateAnnouncedBack && _signalingClient is not null)
            {
                _hostPeerStateAnnouncedBack = true;
                // Notify UI: viewer has connected.
                _uiContext.Post(_ =>
                {
                    ViewerConnectedNotification?.Invoke();
                    ViewerConnectionStatus = "Зритель подключён";
                    ViewerConnectionStatusBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                }, null);
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
                SetStatus($"WS: {msg.Type}", ConnectionState.Connected);
            }, null);
        }
    }

    private int _wsReconnecting;

    private void OnSignalingDisconnected()
    {
        _logService.Warn("UiApp", "ws_disconnected");

        if ((_role != ConnectionRole.Host && _role != ConnectionRole.Viewer) || string.IsNullOrEmpty(_currentSessionId))
        {
            _uiContext.Post(_ => SetStatus("WS отключен", ConnectionState.Error), null);
            return;
        }

        _uiContext.Post(_ => SetStatus("WS отключен, переподключение...", ConnectionState.Connecting), null);

        _ = Task.Run(async () =>
        {
            try { await WsReconnectLoopAsync(); }
            catch { /* ignore */ }
        });
    }

    private async Task WsReconnectLoopAsync()
    {
        if (Interlocked.CompareExchange(ref _wsReconnecting, 1, 0) != 0)
            return; // already reconnecting

        try
        {
            var wsUrl = _currentWsUrl;
            var wsToken = _currentWsToken;
            var sessionId = _currentSessionId;
            var reconnectRole = _role;
            var reconnectFtOnly = _fileTransferOnly;

            int[] backoffMs = [1000, 2000, 4000, 8000, 15000, 30000];

            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (string.IsNullOrEmpty(sessionId))
                    break; // session was closed by user

                // For host: check current role is still host. For viewer: use saved role.
                if (reconnectRole == ConnectionRole.Host && _role != ConnectionRole.Host)
                    break;

                var delay = backoffMs[Math.Min(attempt, backoffMs.Length - 1)];
                _logService.Info("UiApp", $"ws_reconnect_attempt_{attempt + 1}_delay_{delay}ms_role_{reconnectRole}");
                _uiContext.Post(_ => SetStatus($"Переподключение ({attempt + 1}/10)...", ConnectionState.Connecting), null);

                await Task.Delay(delay);

                if (reconnectRole == ConnectionRole.Host && _role != ConnectionRole.Host)
                    break;

                try
                {
                    // Get a fresh WS token via refresh endpoint.
                    var refreshResp = await _sessionApiClient.RefreshSessionAsync(ServerApiBaseUrl, sessionId);
                    var freshToken = refreshResp?.WsToken ?? wsToken;

                    if (reconnectRole == ConnectionRole.Viewer)
                    {
                        // Viewer: full re-connection — new WS + new WebRTC + new DataChannels + new FileTransfer.
                        // ConnectWsAsync calls CleanupConnectionAsync internally.
                        await ConnectWsAsync(wsUrl, freshToken, sessionId, startAsCaller: true, role: ConnectionRole.Viewer, fileTransferOnly: reconnectFtOnly);
                        if (!reconnectFtOnly)
                        {
                            _uiContext.Post(_ => RemoteScreenWindowRequested?.Invoke(), null);
                        }
                        _logService.Info("UiApp", $"viewer_full_reconnect_success_attempt_{attempt + 1}");
                        _uiContext.Post(_ => SetStatus("Переподключено", ConnectionState.Connected), null);
                        return;
                    }

                    // Host: WS-only reconnect (WebRTC is re-created by ResetForNewViewerAsync on new viewer).
                    var oldWs = _signalingClient;
                    _signalingClient = null;
                    if (oldWs is not null)
                    {
                        try
                        {
                            oldWs.MessageReceived -= OnSignalingMessageReceived;
                            oldWs.Disconnected -= OnSignalingDisconnected;
                            await oldWs.DisposeAsync().ConfigureAwait(false);
                        }
                        catch { /* ignore */ }
                    }

                    var newWs = new WebSocketSignalingClient();
                    newWs.MessageReceived += OnSignalingMessageReceived;
                    newWs.Disconnected += OnSignalingDisconnected;
                    await newWs.ConnectAsync(wsUrl, sessionId, freshToken, CancellationToken.None);
                    _signalingClient = newWs;
                    _currentWsToken = freshToken;

                    if (_signalingCoordinator is not null)
                    {
                        _signalingCoordinator.ReplaceSignalingClient(newWs);
                    }

                    // Re-announce host presence.
                    try
                    {
                        await newWs.SendAsync("peer_state", sessionId, new { state = "host_ready" }, CancellationToken.None);
                    }
                    catch { /* best effort */ }

                    _logService.Info("UiApp", $"ws_reconnect_success_attempt_{attempt + 1}");
                    _uiContext.Post(_ => SetStatus("WS переподключен", ConnectionState.Connected), null);
                    return; // success
                }
                catch (Exception ex)
                {
                    _logService.Warn("UiApp", $"ws_reconnect_failed_{ex.Message}");
                }
            }

            _logService.Warn("UiApp", "ws_reconnect_gave_up");
            _uiContext.Post(_ => SetStatus("Не удалось переподключить WS", ConnectionState.Error), null);
        }
        finally
        {
            Interlocked.Exchange(ref _wsReconnecting, 0);
        }
    }
    // ── Seamless ICE upgrade engine ──────────────────────────────────────

    private volatile int _iceUpgradeRunning;
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte> _networkChangedSignal = new();

    /// <summary>Start monitoring for ICE upgrade opportunities. Viewer-only (triggers ICE restart via new offer).</summary>
    private void StartIceUpgradeMonitor(string sessionId, CancellationToken ct)
    {
        // Only the caller (Viewer) can trigger ICE restart by creating a new offer.
        // The Host automatically participates by answering the restarted offer.
        if (_role != ConnectionRole.Viewer) return;
        if (Interlocked.CompareExchange(ref _iceUpgradeRunning, 1, 0) != 0)
            return;
        _ = Task.Run(() => IceUpgradeMonitorLoopAsync(sessionId, ct));

        // Subscribe to OS network change events.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    private void StopIceUpgradeMonitor()
    {
        Interlocked.Exchange(ref _iceUpgradeRunning, 0);
        try { System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged; } catch { }
        try { System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged; } catch { }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _logService.Info("IceUpgrade", "network_address_changed_detected");
        _networkChangedSignal.Enqueue(1);
    }

    private void OnNetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            _logService.Info("IceUpgrade", "network_became_available");
            _networkChangedSignal.Enqueue(1);
        }
    }

    /// <summary>
    /// Unified ICE upgrade monitor. Triggers ICE restart when:
    /// 1) Timer fires (adaptive intervals based on current route quality)
    /// 2) Network interface changes (Ethernet plugged, VPN connected, WiFi switched)
    /// 3) RTT drops significantly (suggests ICE agent found a better path internally)
    ///
    /// ICE restart happens on the EXISTING PeerConnection — zero disruption.
    /// If restart degrades the route, it triggers another restart to recover.
    /// Works for both Viewer and Host roles.
    /// </summary>
    private async Task IceUpgradeMonitorLoopAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            // Initial stabilization delay.
            await Task.Delay(10_000, ct);

            var consecutiveRelayCount = 0;
            var routeBeforeRestart = "unknown";

            while (!ct.IsCancellationRequested && _iceUpgradeRunning == 1)
            {
                var currentRoute = InferRouteType(_localIceCandidateType, _remoteIceCandidateType);
                var currentRtt = _rttMs;
                var isOnBestRoute = IsRouteOptimal(currentRoute);

                // Determine next check interval based on current state.
                var intervalMs = GetAdaptiveInterval(currentRoute, consecutiveRelayCount, currentRtt);

                // Wait for interval OR network change event (whichever comes first).
                var waited = 0;
                var networkTriggered = false;
                while (waited < intervalMs && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                    waited += 500;

                    // Check for network change signal.
                    if (_networkChangedSignal.TryDequeue(out _))
                    {
                        // Drain any additional signals.
                        while (_networkChangedSignal.TryDequeue(out _)) { }
                        // Brief debounce — network events come in bursts.
                        await Task.Delay(3000, ct);
                        networkTriggered = true;
                        break;
                    }
                }

                if (ct.IsCancellationRequested || _iceUpgradeRunning != 1) break;
                if (string.IsNullOrEmpty(sessionId) || (_role != ConnectionRole.Viewer && _role != ConnectionRole.Host))
                    break;

                // Re-check route — might have changed while waiting.
                currentRoute = InferRouteType(_localIceCandidateType, _remoteIceCandidateType);
                if (IsRouteOptimal(currentRoute))
                {
                    // Already on best route. Network change events can be spurious
                    // (DXGI adapter init, VPN enumeration, etc.) — don't break a working connection.
                    // Only restart if RTT has degraded significantly (suggests real connectivity issue).
                    var currentRttCheck = _rttMs;
                    if (!networkTriggered || currentRttCheck < 100)
                    {
                        consecutiveRelayCount = 0;
                        if (networkTriggered)
                            _logService.Info("IceUpgrade", $"skipped_restart_optimal_route_{currentRoute}_rtt={currentRttCheck:F0}ms");
                        continue;
                    }
                }

                // Attempt ICE restart.
                routeBeforeRestart = currentRoute;
                var rttBeforeRestart = _rttMs;
                var trigger = networkTriggered ? "network_change" : $"timer_{intervalMs}ms";
                _logService.Info("IceUpgrade", $"attempting_restart_trigger={trigger}_route={currentRoute}_rtt={rttBeforeRestart:F0}ms");

                var newRoute = await PerformIceRestartAsync(sessionId, ct);

                if (newRoute is null)
                {
                    // Restart failed or cancelled.
                    consecutiveRelayCount++;
                    continue;
                }

                var upgraded = RouteRank(newRoute) < RouteRank(routeBeforeRestart);
                var degraded = RouteRank(newRoute) > RouteRank(routeBeforeRestart);

                if (upgraded)
                {
                    consecutiveRelayCount = 0;
                    var label = IceRouteDisplayLabel(newRoute);
                    _logService.Info("IceUpgrade", $"upgrade_success_{routeBeforeRestart}->{newRoute}");
                    _uiContext.Post(_ => SetStatus($"ICE: {label}", ConnectionState.Connected), null);
                }
                else if (degraded)
                {
                    // Route degraded — immediately try another restart to recover.
                    _logService.Warn("IceUpgrade", $"degraded_{routeBeforeRestart}->{newRoute}_retrying_recovery");
                    var recoveredRoute = await PerformIceRestartAsync(sessionId, ct);
                    if (recoveredRoute is not null)
                        _logService.Info("IceUpgrade", $"recovery_result_{recoveredRoute}");
                    consecutiveRelayCount++;
                }
                else
                {
                    consecutiveRelayCount++;
                    _logService.Info("IceUpgrade", $"no_change_still_{newRoute}_count={consecutiveRelayCount}");
                }

                // Stop trying if we've been on relay for too long with no success.
                if (consecutiveRelayCount >= 10 && !networkTriggered)
                {
                    _logService.Info("IceUpgrade", "giving_up_timer_upgrades_will_react_to_network_changes_only");
                    // Keep running but only react to network changes — set a very long interval.
                    consecutiveRelayCount = 10; // cap to keep interval stable
                }
            }
        }
        catch (OperationCanceledException) { /* session ended */ }
        catch (Exception ex)
        {
            _logService.Warn("IceUpgrade", "monitor_error_" + ex.Message);
        }
        finally
        {
            StopIceUpgradeMonitor();
        }
    }

    /// <summary>Perform a single ICE restart and wait for the route to settle. Returns new route or null on failure.</summary>
    private async Task<string?> PerformIceRestartAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            // Save pre-restart state.
            _localIceCandidateType = "unknown";
            _remoteIceCandidateType = "unknown";
            _signalingCoordinator?.SetIceCandidateFilters(null, null);

            if (_signalingCoordinator is null) return null;

            // Trigger ICE restart — new offer with fresh ice-ufrag/pwd.
            // PeerConnection stays alive, media/data channels unaffected.
            await _signalingCoordinator.StartIceRestartAsCallerAsync(sessionId, ct);

            // Wait for ICE to settle (up to 10s).
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(200, ct);
                var route = InferRouteType(_localIceCandidateType, _remoteIceCandidateType);
                if (!string.Equals(route, "unknown", StringComparison.OrdinalIgnoreCase))
                {
                    // Give it 1 more second for potentially better candidates to arrive.
                    await Task.Delay(1000, ct);
                    return InferRouteType(_localIceCandidateType, _remoteIceCandidateType);
                }
            }

            return InferRouteType(_localIceCandidateType, _remoteIceCandidateType);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logService.Warn("IceUpgrade", "restart_failed_" + ex.Message);
            return null;
        }
    }

    /// <summary>Adaptive interval: relay→short intervals, good route→long/stop.</summary>
    private static int GetAdaptiveInterval(string route, int consecutiveRelayCount, double rttMs)
    {
        if (IsRouteOptimal(route))
            return 300_000; // 5 min — just monitor for degradation

        // Relay: start aggressive (15s), back off with failures.
        return consecutiveRelayCount switch
        {
            0 => 15_000,   // first check after initial 10s delay
            1 => 30_000,
            2 => 60_000,
            3 => 120_000,
            _ => 300_000   // 5 min — mostly waiting for network changes
        };
    }

    private static bool IsRouteOptimal(string route) =>
        string.Equals(route, "host", StringComparison.OrdinalIgnoreCase)
        || string.Equals(route, "srflx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lower = better. Used for upgrade/degrade detection.</summary>
    private static int RouteRank(string route) => route.ToLowerInvariant() switch
    {
        "host" => 1,
        "srflx" => 2,
        "relay" => 3,
        _ => 4
    };
}

