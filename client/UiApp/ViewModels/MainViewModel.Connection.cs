using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FileTransfer;
using SessionClient;
using UiApp;
using UiApp.Dialogs;
using UiApp.Models;
using UiApp.Properties;
using UiApp.Services;
using WebRtcTransport;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel
{
    private async Task CreateSessionAsync()
    {
        if (_role == ConnectionRole.Host && _signalingClient is not null)
        {
            StatusText = Strings.Status_SessionAlreadyExists;
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        SetStatus(Strings.Status_CreatingSession, ConnectionState.Connecting);
        _logService.Info("UiApp", "create_session_clicked");

        var requestedTtlSec = _settings.UnattendedExpiresInSec > 0 ? _settings.UnattendedExpiresInSec : (int?)null;
        var machineId = !string.IsNullOrWhiteSpace(_settings.MachineId) ? _settings.MachineId : null;
        // Pass device_secret so server returns existing session instead of creating new (NET-03).
        // Without this, same machine_id without matching secret → new session with different codes → UI/service code mismatch.
        var deviceSecret = !string.IsNullOrWhiteSpace(_settings.DeviceSecret) ? _settings.DeviceSecret : null;
        var detailed = await _sessionApiClient.CreateSessionAsyncDetailed(ServerApiBaseUrl, AllowUnattended, requestedTtlSec, machineId, deviceSecret);
        if (detailed.Status != CreateSessionStatus.Success)
        {
            // Show proper user-facing сообщение в зависимости от причины rejection'а.
            // Это важно для safety: user должен знать что его IP забанен / сервер на тех.работах,
            // иначе видел бы generic "ошибка создания сессии" и не понимал что делать.
            HandleCreateSessionFailure(detailed);
            return;
        }
        var response = detailed.Response!;

        // Store rotating TURN creds BEFORE WebRTC init, чтобы BuildTransportSettings
        // использовал их при создании PeerConnection'а (иначе получим static creds).
        _dynamicTurnServers = response.TurnServers;
        if (_dynamicTurnServers is not null && _dynamicTurnServers.Count > 0)
        {
            var c = _dynamicTurnServers[0];
            _logService.Info("UiApp", $"turn_creds_received ttl={c.TtlSeconds}s expires_at_unix={c.ExpiresAtUnix}");
        }
        // Notify TURN Status panel чтобы обновилась мгновенно если Settings открыт.
        _uiContext.Post(_ => RefreshTurnStatusBindings(), null);

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
        var ownerSecret = response.OwnerSecret; // save before ConnectWsAsync (which calls CleanupConnectionAsync)
        _fileTransferOnly = FileTransferOnly;
        var wsOk = await ConnectWsAsync(wsUrl, wsToken, sessionId, startAsCaller: false, role: _role, fileTransferOnly: FileTransferOnly);
        if (!wsOk) return; // Deep-1 fix: do not set success status on failure
        _currentOwnerSecret = ownerSecret; // restore AFTER cleanup inside ConnectWsAsync

        // Persist session info so the app can reuse it after restart.
        _settings.LastSessionId = sessionId;
        _settings.LastLoginCode = loginCode;
        _settings.LastPassCode = passCode;
        _settings.LastSessionExpiresAtUtcTicks = DateTime.UtcNow.AddSeconds(expiresInSec).Ticks;
        // Persist device_secret from server — needed for session reuse on next CreateSession call.
        if (!string.IsNullOrEmpty(response.DeviceSecret))
        {
            _settings.DeviceSecret = response.DeviceSecret;
        }
        _settingsService.Save(_settings);

        StartSessionCountdown(expiresInSec);

        _uiContext.Post(_ =>
        {
            SetStatus(Strings.Status_SessionCreated, ConnectionState.Connecting);
        }, null);
        _logService.Info("UiApp", "create_session_success");
    }

    private async Task RefreshSessionAsync()
    {
        if (string.IsNullOrEmpty(_currentSessionId))
        {
            StatusText = Strings.Status_NoActiveSession;
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        _logService.Info("UiApp", "refresh_session_clicked");
        var requestedTtl = _settings.UnattendedExpiresInSec > 0 ? _settings.UnattendedExpiresInSec : (int?)null;
        var response = await _sessionApiClient.RefreshSessionAsync(ServerApiBaseUrl, _currentSessionId, _currentOwnerSecret, requestedTtl, regeneratePass: true);
        if (response is null)
        {
            StatusText = Strings.Status_PasswordRefreshError;
            OnPropertyChanged(nameof(StatusText));
            _logService.Warn("UiApp", "refresh_session_failed");
            return;
        }

        // Persist updated credentials.
        _settings.LastPassCode = response.PassCode;
        _settings.LastSessionExpiresAtUtcTicks = DateTime.UtcNow.AddSeconds(response.ExpiresInSec).Ticks;
        _settingsService.Save(_settings);

        // Rotating TURN: session refresh обязан выдать свежий credential. Обновляем
        // _dynamicTurnServers; mrwebrtc peerConnection будет использовать новые creds
        // при следующем ICE restart / renegotiation.
        //
        // TODO (future): пере-generate ICE config на active peerConnection через
        // SetConfiguration() API + RestartIce для immediate TURN auth refresh без
        // прерывания session. Сейчас новые creds подхватятся при следующем connect.
        if (response.TurnServers is not null && response.TurnServers.Count > 0)
        {
            _dynamicTurnServers = response.TurnServers;
            _logService.Info("UiApp", $"turn_creds_refreshed ttl={response.TurnServers[0].TtlSeconds}s");
            _uiContext.Post(_ => RefreshTurnStatusBindings(), null);
        }

        StartSessionCountdown(response.ExpiresInSec);

        _uiContext.Post(_ =>
        {
            LoginCode = response.LoginCode;
            PassCode = response.PassCode;
            OnPropertyChanged(nameof(LoginCode));
            OnPropertyChanged(nameof(PassCode));
            SetStatus(Strings.Status_PasswordRefreshed, ConnectionState.Connecting);
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
            StatusText = Strings.Status_RemoteScreenOpened;
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        if (JoinLoginCode.Length != 8 || JoinPassCode.Length != 8 || !JoinLoginCode.All(char.IsDigit) || !JoinPassCode.All(char.IsDigit))
        {
            StatusText = Strings.Status_InvalidCredentials;
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
        // IsJoiningAsViewer управляет ProgressBar на Connect button — включаем
        // сейчас, выключаем в finally чтобы анимация останавливалась на любом exit
        // (success, API fail, WS fail, exception).
        IsJoiningAsViewer = true;
        try
        {
            // Bug fix (frozen remote screen): на случай если после прошлого connect
            // остался старый кадр в bitmap'е — сбросить до начала попытки. Если connect
            // упадёт — пользователь увидит overlay "Waiting..." вместо ложно-успешного
            // замороженного экрана.
            if (RemoteFrameImage is not null)
            {
                _remoteFrameBitmap = null;
                RemoteFrameImage = null;
                _uiContext.Post(_ => OnPropertyChanged(nameof(RemoteFrameImage)), null);
            }

            ConnectionStep = 1; // Signaling — WS + API handshake
            SetStatus(Strings.Status_Connecting, ConnectionState.Connecting);

            var detailed = await _sessionApiClient.JoinSessionAsyncDetailed(serverBaseUrl, loginCode, passCode);
            if (detailed.Status != JoinSessionStatus.Success)
            {
                ConnectionStep = 0;
                // Show specific user-facing message by rejection reason (banned, locked, blocked).
                HandleJoinFailure(detailed, loginCode);
                return;
            }
            var response = detailed.Response!;

            // Store rotating TURN creds BEFORE WebRTC init (см. CreateSession flow).
            _dynamicTurnServers = response.TurnServers;
            if (_dynamicTurnServers is not null && _dynamicTurnServers.Count > 0)
            {
                _logService.Info("UiApp", $"turn_creds_received_viewer ttl={_dynamicTurnServers[0].TtlSeconds}s");
            }
            _uiContext.Post(_ => RefreshTurnStatusBindings(), null);

            var wsUrl = ResolveWsUrl(serverBaseUrl, response.WsUrl);
            _role = ConnectionRole.Viewer;
            _fileTransferOnly = FileTransferOnly;
            ConnectionStep = 2; // ICE negotiation start
            var wsOk = await ConnectWsWithAutoIcePriorityAsync(wsUrl, response.WsToken, response.SessionId, role: _role, fileTransferOnly: FileTransferOnly);
            if (!wsOk) { ConnectionStep = 0; return; } // Deep-1 fix: don't set success status on failure
            ConnectionStep = 3; // DataChannel open
            _viewerJoinLoginCode = loginCode;  // restore AFTER cleanup inside ConnectWsAsync
            _viewerJoinPassCode = passCode;
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
            ConnectionStep = 4; // Ready — hides stepper via ConnectionStepVisible computed
            SetStatus($"Подключено: {label}", ConnectionState.Connected);
            _logService.Info("UiApp", "join_success name=" + label);

            // Ad-hoc connect (вкладка «Подключиться» без выбора контакта) регистрируется
            // в ОТДЕЛЬНОЙ истории — RecentHistoryService (recent-history.json), НЕ в
            // Address Book. Это не засоряет контакты — пользователь явно решает что
            // сохранить как Contact (через кнопку в Recent card).
            // Skip если _currentUnattendedContact != null — это address-book подключение,
            // handle'ится ConnectToSpecificContactAsync (обновление Contact.LastConnectedUtc).
            if (_currentUnattendedContact is null)
            {
                RegisterRecentAdhocConnection(loginCode, passCode);
            }
        }
        finally
        {
            IsJoiningAsViewer = false;
        }
    }

    // (AutoSaveAdhocConnection + TryPromptRenameAdhocContact удалены — ad-hoc
    // подключения теперь идут в RecentHistoryService, НЕ в Address Book.
    // См. RegisterRecentAdhocConnection в MainViewModel.AddressBook.cs.)

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
    private async Task<bool> ConnectWsWithAutoIcePriorityAsync(string wsUrlFromServer, string wsToken, string sessionId, ConnectionRole role, bool fileTransferOnly = false)
    {
        // Phase 1: Establish WS + WebRTC with ALL ICE methods enabled.
        // No filtering — let the ICE agent do its job with all candidate types.
        var ok = await ConnectWsAsync(wsUrlFromServer, wsToken, sessionId, startAsCaller: false, role: role, fileTransferOnly: fileTransferOnly);
        if (!ok) return false; // Deep-1 fix: propagate failure

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
            var label = IceRouteDisplayLabel(route, _localIceCandidateIp, _remoteIceCandidateIp);
            _logService.Info("WebRTC", $"auto_ice_connected_via_{route}_local={_localIceCandidateIp}_remote={_remoteIceCandidateIp}");
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
        if (_role == ConnectionRole.Viewer)
        {
            _iceUpgradeOptimizer?.Dispose();
            _iceUpgradeOptimizer = new Services.IceUpgradeOptimizer(
                getLocalIceType:  () => _localIceCandidateType,
                getRemoteIceType: () => _remoteIceCandidateType,
                getRttMs:         () => (int)_rttMs,
                restartIceAsync:  async () =>
                {
                    _localIceCandidateType = "unknown";
                    _remoteIceCandidateType = "unknown";
                    _signalingCoordinator?.SetIceCandidateFilters(null, null);
                    if (_signalingCoordinator is not null)
                        await _signalingCoordinator.StartIceRestartAsCallerAsync(sessionId, _lifetimeCts.Token);
                },
                onLog: msg => _logService.Info("IceUpgrade", msg));
            _iceUpgradeOptimizer.Start(_lifetimeCts.Token);
        }
        return true;
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
                try
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
                }
                catch (OperationCanceledException) { }
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
            finally
            {
                // Deep-5 fix: cancel pollTask and wait for it to complete.
                cts.Cancel();
                try { await pollTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
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
        WireDataChannelHandlers(_dataChannelCoordinator!, role);
        CreateFileTransferService(_dataChannelCoordinator!);

        // Host: каждый раз когда Control DataChannel открывается (т.е. viewer подключился
        // и SCTP/DTLS handshake завершился) — проактивно отправить screen_meta + host_displays.
        // Setup-loop (SendInitDataChannelMessagesWithRetryAsync) запускается один раз при
        // ws_connected и живёт всего 60с: если viewer подойдёт позже (часто в unattended
        // режиме host висит часами до первого подключения), setup-loop давно умер, а Fix A
        // в ConfigureLocalVideoAsync стреляет слишком рано (DC ещё в Connecting) — и
        // screen_meta не отправляется никогда, пока не сменят качество.
        if (role == ConnectionRole.Host)
        {
            _dataChannelCoordinator!.SubscribeChannelOpened(OnHostControlChannelOpenedForInitialSends);
        }
    }

    private void OnHostControlChannelOpenedForInitialSends(DataChannelKind kind)
    {
        if (kind != DataChannelKind.Control) return;
        var dc = _dataChannelCoordinator;
        if (dc is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var meta = _localScreenMeta;
                if (meta is not null)
                {
                    await dc.SendScreenMetaAsync(meta, _lifetimeCts.Token);
                    _logService.Info("ScreenCapture", "screen_meta_sent_on_control_open");
                }
                else
                {
                    _logService.Debug("ScreenCapture", "screen_meta_skip_no_local_meta_on_control_open");
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex) { _logService.Warn("ScreenCapture", "screen_meta_send_on_open_failed_" + ex.Message); }

            try
            {
                await SendHostDisplaysIfHostAsync(_lifetimeCts.Token);
                _logService.Debug("UiApp", "host_displays_sent_on_control_open");
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex) { _logService.Warn("UiApp", "host_displays_send_on_open_failed_" + ex.Message); }
        }, _lifetimeCts.Token);
    }

    /// <summary>Subscribe all DataChannelCoordinator event handlers (input, screen meta, clipboard, ping/pong, remote directory protocol).</summary>
    private void WireDataChannelHandlers(DataChannelCoordinator dc, ConnectionRole role)
    {
        if (role == ConnectionRole.Host)
        {
            dc.MouseReceived += OnMouseInputReceived;
            dc.KeyboardReceived += OnKeyboardInputReceived;
            dc.LaunchProcessRequested += OnLaunchProcessRequestReceived;
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
            if (_pendingDeleteRequestIds.TryRemove(p.RequestId ?? "", out _))
            {
                DeleteResponseReceived?.Invoke(p);
            }
        };
        dc.FolderDownloadRequestReceived += OnFolderDownloadRequestReceived;
        dc.RenameRequestReceived += OnRenameRequestReceived;
        dc.RenameResponseReceived += p => RenameResponseReceived?.Invoke(p);
    }

    /// <summary>Create FileTransferService and wire its progress/completion/failure events.</summary>
    private void CreateFileTransferService(DataChannelCoordinator dc)
    {
        var fileChannel = new DataChannelFileTransferChannel(dc);
        var defaultSaveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ZConectReceived");
        _incomingSaveDirHolder = new IncomingSaveDirHolder { Path = defaultSaveDir };
        _outgoingTargetDirHolder = new OutgoingTargetDirHolder();
        _incomingSaveDirPath = defaultSaveDir;
        _fileTransferService = new FileTransferService(fileChannel,
            getIncomingSaveDir: () => !string.IsNullOrEmpty(_incomingSaveDirPath) ? _incomingSaveDirPath : defaultSaveDir,
            getOutgoingTargetDir: () => _outgoingTargetDirHolder?.Path?.Trim() ?? string.Empty,
            onLog: (cat, ev, args) =>
                _logService.Info(cat, args.Length > 0 ? ev + " " + string.Join(" ", args) : ev),
            // Viewer сидит за компьютером и нажимает кнопки в FT — dialog показывается
            // здесь локально для incoming (download) файлов. Host никогда не показывает
            // local dialog: query уходит к viewer-sender (для upload'ов).
            canShowLocalDialogForIncoming: _role == ConnectionRole.Viewer);

        // FT conflict dialog routing (refactor 2026-04-19, v2).
        // Dialog ВСЕГДА показывается на стороне Viewer — там user сидит за компьютером
        // и нажимает кнопки в FT. Есть два пути:
        //
        // (A) Viewer = receiver (download с host'а): OnFileConflict local callback показывает
        //     dialog прямо здесь. Активируется только при _role == Viewer (canShowLocalDialog).
        //
        // (B) Viewer = sender (upload на host): Host-receiver не может показать dialog сам
        //     (не initiator), шлёт FileConflictQuery через DC → Viewer получает event,
        //     показывает dialog, шлёт response → Host применяет action.
        //
        // Wire оба handler'а всегда; активирован только правильный per-transfer в зависимости
        // от role. Раньше был баг (v1): dialog всегда на receiver → download с host показывал
        // dialog на host'е (не у user'а). Upload c host'а на viewer — наоборот, правильно v1.

        Window? FindDialogOwner()
        {
            Window? owner = null;
            foreach (Window w in System.Windows.Application.Current.Windows)
            {
                if (w is FileTransferWindow ftw && ftw.IsVisible) { owner = ftw; break; }
            }
            return owner ?? System.Windows.Application.Current.MainWindow;
        }

        // (A) Receiver-as-Viewer (download): local dialog.
        _fileTransferService.OnFileConflict = path =>
        {
            var result = FileTransfer.ConflictAction.Rename;
            try
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    result = FileTransferWindow.ShowFileConflictDialogStatic(path, FindDialogOwner());
                });
            }
            catch (Exception ex)
            {
                _logService.Warn("FT", "file_conflict_dialog_local_failed_" + ex.Message);
            }
            return result;
        };

        // (B) Sender-as-Viewer (upload): remote query from host-receiver.
        _fileTransferService.FileConflictQueryReceived += query =>
        {
            var result = FileTransfer.ConflictAction.Rename;
            try
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    result = FileTransferWindow.ShowFileConflictDialogStatic(query.FileName, FindDialogOwner());
                });
            }
            catch (Exception ex)
            {
                _logService.Warn("FT", "file_conflict_dialog_remote_failed_" + ex.Message);
            }
            var applyToAll = result is FileTransfer.ConflictAction.OverwriteAll or FileTransfer.ConflictAction.SkipAll;
            var fts = _fileTransferService;
            if (fts is null) return;
            // Fire-and-forget + catch to prevent UnobservedTaskException если DC is Closing
            // (observed host logs 2026-04-19). Если send fails — виндовсь уже мёртв, log warn.
            _ = fts.SendConflictResponseAsync(query.TransferId, result, applyToAll)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _logService.Warn("FT", "conflict_response_send_failed_" + (t.Exception?.InnerException?.Message ?? "unknown"));
                }, TaskScheduler.Default);
        };
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
    }

    /// <summary>Returns false if connection failed (caller must not set success status).</summary>
    /// <summary>Audit fix #3 2026-04-25: refresh WS token при auth failure. Returns fresh
    /// ws_token или null если не получилось. Host: /session/refresh с owner_secret.
    /// Viewer: /session/join с сохранёнными login+pass codes.</summary>
    private async Task<string?> TryRefreshWsTokenAsync(ConnectionRole role, string sessionId, CancellationToken ct)
    {
        try
        {
            if (role == ConnectionRole.Host && !string.IsNullOrEmpty(_currentOwnerSecret))
            {
                var refreshed = await _sessionApiClient.RefreshSessionAsyncDetailed(
                    ServerApiBaseUrl, sessionId, _currentOwnerSecret, ct: ct).ConfigureAwait(false);
                if (refreshed.Status == SessionClient.RefreshStatus.Success && refreshed.Response is not null)
                {
                    // Apply fresh rotating TURN creds если пришли — same as auto-refresh path.
                    if (refreshed.Response.TurnServers is { Count: > 0 } trs)
                    {
                        _dynamicTurnServers = trs;
                        _uiContext.Post(_ => RefreshTurnStatusBindings(), null);
                    }
                    return refreshed.Response.WsToken;
                }
                return null;
            }
            if (role == ConnectionRole.Viewer
                && !string.IsNullOrEmpty(_viewerJoinLoginCode)
                && !string.IsNullOrEmpty(_viewerJoinPassCode))
            {
                var rejoin = await _sessionApiClient.JoinSessionAsync(
                    ServerApiBaseUrl, _viewerJoinLoginCode, _viewerJoinPassCode, ct).ConfigureAwait(false);
                if (rejoin is not null)
                {
                    if (rejoin.TurnServers is { Count: > 0 } trs)
                    {
                        _dynamicTurnServers = trs;
                        _uiContext.Post(_ => RefreshTurnStatusBindings(), null);
                    }
                    return rejoin.WsToken;
                }
                return null;
            }
        }
        catch (Exception ex)
        {
            _logService.Warn("UiApp", $"try_refresh_ws_token_exception: {ex.Message}");
        }
        return null;
    }

    private async Task<bool> ConnectWsAsync(string wsUrlFromServer, string wsToken, string sessionId, bool startAsCaller, ConnectionRole role, bool fileTransferOnly = false)
    {
        // 0. Consume pre-created agents BEFORE cleanup — cleanup cancels _lifetimeCts
        //    which would kill the preload task if it's still running.
        IPeerConnectionAgent? preloadedPeer = null;
        IDataChannelAgent? preloadedData = null;
        if (_preCreatedAgentsTask is not null)
        {
            try { (preloadedPeer, preloadedData) = await _preCreatedAgentsTask; }
            catch { /* preload failed — will create fresh below */ }
            _preCreatedAgentsTask = null;
            // Detach from _realPeerAgent so CleanupConnectionAsync doesn't dispose
            // the preloaded agent — we'll re-attach it in step 5.
            if (preloadedPeer is not null) _realPeerAgent = null;
        }

        // 1. Cleanup previous connection.
        await CleanupConnectionAsync().ConfigureAwait(false);
        _lifetimeCts = new CancellationTokenSource();

        // 2. Reset all connection state fields.
        ResetConnectionState(sessionId, role, fileTransferOnly);

        // 3. Resolve WS URL.
        var wsUrl = wsUrlFromServer;
        if (string.IsNullOrWhiteSpace(wsUrl) || wsUrl == "/ws")
        {
            wsUrl = WebSocketUrl;
        }
        _currentWsUrl = wsUrl;
        _currentWsToken = wsToken;

        // 4. Create signaling client.
        _signalingClient = new WebSocketSignalingClient();
        _signalingClient.MessageReceived += OnSignalingMessageReceived;
        _signalingClient.Disconnected += OnSignalingDisconnected;
        _logService.Debug("Events", "signaling_events_subscribed");

        // 5. Create and initialize WebRTC peer agent — reuse pre-created if available.
        //    Discard preloaded agent if TURN credentials changed since preload (e.g. pipe
        //    config arrived with fresh credentials, or service-pushed password differs
        //    from disk-cached one). Without this, preload uses stale password → no relay
        //    candidates → ICE fails repeatedly on first few sessions until recreate.
        IPeerConnectionAgent? peerAgent;
        IDataChannelAgent? dataAgent;
        var preloadStale = preloadedPeer is MixedRealityPeerConnectionAgent mra
            && !string.IsNullOrEmpty(TurnUrl)
            && (string.IsNullOrEmpty(mra.Settings?.TurnPassword)
                || !string.Equals(mra.Settings?.TurnPassword, TurnPassword, StringComparison.Ordinal)
                || !string.Equals(mra.Settings?.TurnUsername, TurnUsername, StringComparison.Ordinal)
                || !string.Equals(mra.Settings?.TurnUrl, TurnUrl, StringComparison.Ordinal));
        if (preloadedPeer is not null && preloadedData is not null && !preloadStale)
        {
            peerAgent = preloadedPeer;
            dataAgent = preloadedData;
            // Restore _realPeerAgent since CleanupConnectionAsync nulled it.
            _realPeerAgent = preloadedPeer as MixedRealityPeerConnectionAgent;
            _logService.Debug("UiApp", "webrtc_preloaded_agent_reused");
        }
        else
        {
            if (preloadStale) _logService.Debug("UiApp", "webrtc_preload_discarded_turn_credentials_changed");
            (peerAgent, dataAgent) = await CreateAndInitPeerAgentAsync(fileTransferOnly);
        }
        if (peerAgent is null || dataAgent is null) return false; // WebRTC init failed

        // 6. Create SignalingCoordinator.
        _signalingCoordinator = new SignalingCoordinator(
            _signalingClient,
            peerAgent,
            msg => _logService.Debug("Signaling", msg),
            beforeCreateAnswerAsync: async (offerSdp) =>
            {
                if (_role == ConnectionRole.Host)
                {
                    // NET-05: confirmation gate — ask host before accepting viewer.
                    if (_settings.RequireConfirmation && !_viewerApproved)
                    {
                        var approved = await ShowViewerConfirmationAsync();
                        if (!approved)
                        {
                            _logService.Info("UiApp", "viewer_confirm_rejected");
                            // Notify viewer explicitly so его UI abort'ит сразу, не ждёт answer timeout.
                            try
                            {
                                await _signalingClient!.SendAsync("peer_state", _currentSessionId,
                                    new { state = "rejected" }, _lifetimeCts.Token);
                            }
                            catch { /* best effort — viewer упадёт по WS timeout если это не дошло */ }
                            throw new OperationCanceledException("viewer_rejected_by_host");
                        }
                        _viewerApproved = true;
                        _logService.Info("UiApp", "viewer_confirm_approved");
                    }
                    // Вне зависимости от того как approve произошёл (confirmation dialog /
                    // password / auto-approve), триггерим "Зритель подключён" уведомление.
                    // NotifyViewerFullyApproved idempotent — если password handler уже
                    // его вызвал (с синим статусом), повторный вызов no-op.
                    NotifyViewerFullyApproved();
                }

                var offerHasVideo = offerSdp.Contains("m=video", StringComparison.OrdinalIgnoreCase);
                if (_role == ConnectionRole.Host && !_hostVideoStarted && _realPeerAgent is not null && offerHasVideo)
                {
                    _hostVideoStarted = true;
                    await ConfigureLocalVideoAsync(_realPeerAgent);
                    StartAutoQualityLoopIfNeeded();
                    StartRttLoopIfNeeded();
                    StartQualityStatsLoopIfNeeded();
                    _logService.Info("ScreenCapture", "local_video_started_on_offer");
                }
            },
            shouldSendLocalIceCandidate: null,
            shouldAcceptRemoteIceCandidate: null);
        _signalingCoordinator.IceCandidateObserved += OnIceCandidateObserved;
        _signalingCoordinator.SetSession(sessionId);

        // 7. Create DataChannelCoordinator and wire handlers.
        _isDataChannelReal = dataAgent is not MockDataChannelAgent;
        _dataChannelCoordinator = new DataChannelCoordinator(
            dataAgent,
            msg => _logService.Debug("DataChannel", msg));
        WireUpDataChannelCoordinator(role);
        _logService.Debug("Events", "data_channel_wired");

        // 7.5. Unattended password gate (pre-offer handshake поверх WS relay).
        //      Host: подписаны на viewer probe/password/skip events → respond с mode / verify / result.
        //      Viewer: RunViewerUnattendedHandshakeAsync вызовется ниже после WS connect, до offer.
        InitializeUnattendedAuth(sessionId);

        // 8. Connect WS and handle deferred caller start.
        try
        {
            await _signalingClient.ConnectAsync(wsUrl, sessionId, wsToken);
            _logService.Info("UiApp", "ws_connected");
            await _signalingClient.SendAsync("peer_state", sessionId, new { state = "joined" });

            // Audit fix #3 2026-04-25: detect auth failure (server close 4001) и
            // proactive refresh + retry once. Без этого если token expire'нул в окне
            // между /join и WS connect (60s TTL), user видит "Connected → cycle"
            // т.к. background reconnect manager kick'ает только после Disconnected
            // event.
            //
            // Server sends close 4001 если token invalid → ReceiveLoop captures →
            // LastDisconnectWasAuthFailure=true. Ждём 600ms (round-trip + processing
            // overhead), если detected — refresh token + recreate WS.
            await Task.Delay(600, _lifetimeCts.Token);
            if (_signalingClient is not null && _signalingClient.LastDisconnectWasAuthFailure)
            {
                _logService.Info("UiApp", "ws_auth_token_expired_attempting_refresh");
                var fresh = await TryRefreshWsTokenAsync(role, sessionId, _lifetimeCts.Token);
                if (string.IsNullOrEmpty(fresh))
                {
                    _logService.Warn("UiApp", "ws_token_refresh_failed_after_auth_failure");
                    SetStatus(Strings.Status_SessionExpired_NewRequired, ConnectionState.Error);
                    return false;
                }
                // Tear down dead WS, recreate с fresh token.
                try
                {
                    _signalingClient.MessageReceived -= OnSignalingMessageReceived;
                    _signalingClient.Disconnected -= OnSignalingDisconnected;
                    await _signalingClient.DisposeAsync().ConfigureAwait(false);
                }
                catch { /* ignore */ }
                _signalingClient = new WebSocketSignalingClient();
                _signalingClient.MessageReceived += OnSignalingMessageReceived;
                _signalingClient.Disconnected += OnSignalingDisconnected;
                await _signalingClient.ConnectAsync(wsUrl, sessionId, fresh);
                await _signalingClient.SendAsync("peer_state", sessionId, new { state = "joined" });
                _currentWsToken = fresh;
                wsToken = fresh;
                // Bug fix 2026-04-27: UnattendedAuthCoord/SignalingCoordinator должны
                // получить новый ws — иначе их subscription dangles.
                _signalingCoordinator?.ReplaceSignalingClient(_signalingClient);
                _unattendedAuthCoord?.ReplaceSignalingClient(_signalingClient);
                _logService.Info("UiApp", "ws_reconnected_with_refreshed_token");
            }

            // Viewer unattended handshake — всегда после WS connect, ДО offer.
            // Запускаем по role, не по startAsCaller: offer может стартовать из ConnectWsAsync (reconnect path)
            // ИЛИ из ConnectWsWithAutoIcePriorityAsync (initial join path с startAsCaller=false).
            if (role == ConnectionRole.Viewer)
            {
                var handshakeOk = await RunViewerUnattendedHandshakeAsync(_lifetimeCts.Token);
                if (!handshakeOk)
                {
                    _logService.Warn("UiApp", "viewer_handshake_aborted_connect");
                    return false;
                }
            }

            if (startAsCaller)
            {
                await HandleDeferredCallerStartAsync(sessionId, role);
            }
        }
        catch (Exception ex)
        {
            _logService.Error("UiApp", "ws_connect_failed", ex.Message);
            SetStatus("WS подключение не удалось", ConnectionState.Error);
            return false;
        }

        // 9. Start init messages and sync loops.
        _ = Task.Run(() => SendInitDataChannelMessagesWithRetryAsync(role, _lifetimeCts.Token), _lifetimeCts.Token);
        StartClipboardSyncLoopIfNeeded();
        StartCursorSyncLoopIfNeeded();
        return true;
    }

    /// <summary>Reset all connection-related fields to initial state before a new connection attempt.</summary>
    private void ResetConnectionState(string sessionId, ConnectionRole role, bool fileTransferOnly)
    {
        _role = role;
        _fileTransferOnly = fileTransferOnly;
        _currentSessionId = sessionId;
        _callerStartDeferred = false;
        Interlocked.Exchange(ref _callerStartedFlag, 0);
        _hostPeerStateAnnouncedBack = false;
        _hostVideoStarted = false;
        _remoteScreenMeta = null;
        _localScreenMeta = null;
        _mouseInputBlockedWarnedNoMeta = false;
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
    }

    /// <summary>Build TransportSettings from current user settings: primary STUN/TURN + failover TurnServers + toggles.
    /// Если сервер выдал rotating TURN credentials в session response (_dynamicTurnServers не null),
    /// они используются вместо static config — primary TURN overriding'ся, legacy static ignored.</summary>
    private TransportSettings BuildTransportSettings()
    {
        var servers = new List<TurnServerEntry>();

        // Priority 1: rotating creds из session response (RFC 7635 / server).
        // Перекрывают static config полностью когда active — мы не хотим чтобы stale
        // static creds мешали HMAC-based auth.
        //
        // Thread-safety: WsReconnectionManager viewer_rejoin и user-triggered
        // RefreshSession могут одновременно писать _dynamicTurnServers. Snapshot
        // reference один раз и дальше работаем с local копией — .NET object reads/writes
        // ссылок atomic на x86/x64/ARM, но чтение из разных мест в одном BuildTransportSettings'е
        // могло бы дать несогласованное состояние (Null reference если swap между null checks).
        var dynSnapshot = _dynamicTurnServers;
        string primaryUrl = TurnUrl;
        string primaryUser = TurnUsername;
        string primaryPass = TurnPassword;
        if (dynSnapshot is not null && dynSnapshot.Count > 0)
        {
            var first = dynSnapshot[0];
            if (first.Urls.Count > 0)
            {
                primaryUrl = first.Urls[0];
                primaryUser = first.Username;
                primaryPass = first.Credential;
                // Дополнительные rotating servers (если server вернул >1) добавляются в failover list
                for (int i = 1; i < dynSnapshot.Count; i++)
                {
                    var ds = dynSnapshot[i];
                    if (ds.Urls.Count == 0) continue;
                    servers.Add(new TurnServerEntry
                    {
                        Url = ds.Urls[0],
                        Username = ds.Username,
                        Password = ds.Credential
                    });
                }
                _logService.Debug("WebRTC", $"using_rotating_turn_creds ttl={first.TtlSeconds}s expires_at={first.ExpiresAtUnix}");
            }
            else
            {
                // FINDING M3: server sent turn_servers entry с empty urls — потенциальный
                // silent downgrade на static creds. Warn чтобы видно в логах.
                _logService.Warn("WebRTC", "rotating_turn_first_entry_has_empty_urls — fallback to static TurnUrl");
            }
        }
        // Priority 2: static TurnServers failover list (legacy). Добавляются в конец.
        if (_settings.TurnServers is not null)
        {
            foreach (var s in _settings.TurnServers)
            {
                if (s is null) continue;
                servers.Add(new TurnServerEntry
                {
                    Url = s.Url ?? string.Empty,
                    Username = s.Username ?? string.Empty,
                    Password = s.Password ?? string.Empty
                });
            }
        }
        return new TransportSettings
        {
            StunUrl = StunUrl,
            TurnUrl = primaryUrl,
            TurnUsername = primaryUser,
            TurnPassword = primaryPass,
            TurnServers = servers,
            PreferRelay = _settings.PreferRelay,
            PreferLanVpnNoTurn = _settings.PreferLanVpnNoTurn
        };
    }

    /// <summary>Create and initialize WebRTC peer agent + data channel agent, subscribe events. Returns (null, null) on failure.</summary>
    private async Task<(IPeerConnectionAgent?, IDataChannelAgent?)> CreateAndInitPeerAgentAsync(bool fileTransferOnly)
    {
        IPeerConnectionAgent peerAgent;
        IDataChannelAgent dataAgent;
        try
        {
            _realPeerAgent = new MixedRealityPeerConnectionAgent(BuildTransportSettings(), msg => _logService.Debug("WebRTC", msg));
            var realDataAgent = new MixedRealityDataChannelAgent();
            _realPeerAgent.DataChannelAdded += ch => realDataAgent.AttachChannel(ch);
            await _realPeerAgent.InitializeAsync(_lifetimeCts.Token, includeVideoTransceiver: !fileTransferOnly);

            peerAgent = _realPeerAgent;
            dataAgent = realDataAgent;
            _logService.Info("UiApp", "webrtc_real_agent_enabled");
        }
        catch (Exception ex)
        {
            // F-04 fix: fail-fast instead of silent mock fallback.
            _logService.Error("UiApp", "webrtc_init_failed", ex.Message);
            SetStatus("WebRTC недоступен: " + ex.Message, ConnectionState.Error);
            return (null, null);
        }

        peerAgent.RemoteVideoFrameReceived += OnRemoteVideoFrameReceived;
        peerAgent.IceStateFailed += OnIceStateFailed;
        peerAgent.IceStateDisconnected += OnIceStateDisconnected;
        peerAgent.IceStateReconnected += OnIceStateReconnected;
        if (_realPeerAgent is not null)
        {
            _realPeerAgent.DesktopAccessLost += OnDesktopAccessLost;
            _realPeerAgent.DesktopReinited += OnDesktopReinited;
            _realPeerAgent.TrySwitchToActiveDesktop = SwitchCaptureThreadToActiveDesktop;
        }
        _logService.Debug("Events", "peer_agent_events_subscribed");

        return (peerAgent, dataAgent);
    }

    /// <summary>Handle deferred caller start logic: defer offer until peer_state received, with 1.5s fallback timeout.</summary>
    private async Task HandleDeferredCallerStartAsync(string sessionId, ConnectionRole role)
    {
        // Our WS server is a pure relay (no queue). If caller sends offer before the other peer connects,
        // the offer is lost. So we defer offer until we see a peer_state from the other side.
        _callerStartDeferred = role == ConnectionRole.Viewer;
        if (!_callerStartDeferred)
        {
            await _signalingCoordinator!.StartAsCallerAsync(sessionId);
            Interlocked.Exchange(ref _callerStartedFlag, 1);
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

                if (Interlocked.CompareExchange(ref _callerStartedFlag, 1, 0) == 0 && _signalingCoordinator is not null)
                {
                    try
                    {
                        await _signalingCoordinator.StartAsCallerAsync(_currentSessionId, _lifetimeCts.Token);
                        Interlocked.Exchange(ref _callerStartedFlag, 1);
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
        try { _iceUpgradeOptimizer?.Dispose(); _iceUpgradeOptimizer = null; } catch { /* ignore */ }

        // Tear down unattended auth coordinator (host subscribers + viewer pending state).
        try { DisposeUnattendedAuthCoord(); } catch { /* ignore */ }

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
                _logService.Debug("Events", "signaling_events_unsubscribed");
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
                _realPeerAgent.IceStateFailed -= OnIceStateFailed;
                _realPeerAgent.IceStateDisconnected -= OnIceStateDisconnected;
                _realPeerAgent.IceStateReconnected -= OnIceStateReconnected;
                _realPeerAgent.DesktopAccessLost -= OnDesktopAccessLost;
                _realPeerAgent.DesktopReinited -= OnDesktopReinited;
                _logService.Debug("Events", "peer_agent_events_unsubscribed");
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

        // BUGFIX 2026-04-24: известно что mrwebrtc.dll может повиснуть в DllMain
        // при PeerConnection.Dispose() (native deadlock на shutdown thread). Если это
        // произойдёт внутри CleanupConnectionAsync, следующая логика (RemoteScreenWindowShouldHide,
        // ConnectionState.Idle) никогда не выполнится → orphaned Remote Desktop window
        // остаётся открытой, UI переходит в limbo state.
        //
        // Fix:
        //   1. Fire RemoteScreenWindowShouldHide СРАЗУ (до dispose) — гарантирует что
        //      окно закроется независимо от того, подвиснет ли native код.
        //   2. Dispose _realPeerAgent С ТАЙМАУТОМ 3 сек — если mrwebrtc deadlock'ает,
        //      abandon reference (GC не соберёт из-за pinned handles, но хотя бы
        //      managed state consistent).
        //   3. Same для ws.DisposeAsync() — 2 сек timeout на graceful close, потом abandon.

        // Step 1: Close Remote Desktop window первым (safe, только WPF-операции).
        _uiContext.Post(_ =>
        {
            try { RemoteScreenWindowShouldHide?.Invoke(); } catch { /* ignore */ }
        }, null);

        // Step 2: Dispose WS с таймаутом.
        var ws = _signalingClient;
        _signalingClient = null;
        if (ws is not null)
        {
            try
            {
                var disposeTask = ws.DisposeAsync().AsTask();
                if (await Task.WhenAny(disposeTask, Task.Delay(2000)).ConfigureAwait(false) != disposeTask)
                {
                    _logService.Warn("UiApp", "ws_dispose_timeout_2s_abandoning");
                }
            }
            catch
            {
                // ignore
            }
        }

        // Step 3: Dispose peer agent с таймаутом 3 сек.
        // mrwebrtc.PeerConnection.Dispose() иногда deadlock'ает в native код на shutdown —
        // если это происходит, abandon и двигаемся дальше. Остальная managed cleanup
        // гарантированно выполнится + orphaned thread'ы рано или поздно завершатся (либо
        // процесс TerminateProcess на полном Exit).
        var peerToDispose = _realPeerAgent;
        if (peerToDispose is not null)
        {
            var disposeTask = Task.Run(() =>
            {
                try { peerToDispose.Dispose(); } catch { /* ignore */ }
            });
            if (await Task.WhenAny(disposeTask, Task.Delay(3000)).ConfigureAwait(false) != disposeTask)
            {
                _logService.Warn("UiApp", "peer_agent_dispose_timeout_3s_abandoning_orphan_native_threads");
                // Don't await — abandon. Managed side continues cleanup.
            }
        }

        _dataChannelCoordinator = null;
        _signalingCoordinator = null;
        _realPeerAgent = null;
        _isDataChannelReal = false;
        _pendingDirListRequestId = null;
        _pendingCreateFolderRequestId = null;
        _pendingDeleteRequestIds.Clear();
        _role = ConnectionRole.None;
        _currentSessionId = string.Empty;
        _currentOwnerSecret = string.Empty;
        // NOTE: _viewerJoinLoginCode NOT cleared — needed by reconnect loop
        // that may start from a disconnect event INSIDE ConnectWsAsync.
        _wsReconnectionManager.ResetP2PActivity();
        _inputSenderTask = null; // Deep-2 fix: allow input sender to restart after reconnect
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
        _wsReconnectionManager.Reset();
        _lastObservedClipboardText = string.Empty;
        _lastSentClipboardText = string.Empty;
        _lastAppliedRemoteClipboardText = string.Empty;
        _lastAppliedRemoteClipboardAtUtc = DateTime.MinValue;
        _lastSentCursorType = string.Empty;
        _viewerApproved = false; // NET-05: require fresh confirmation on next connection
        _viewerConnectedNotified = false; // след. viewer → свежий "Зритель подключён" toast
        ResetVideoStats();
        // Bug fix (frozen remote screen): сбросить последний кадр, иначе RemoteScreenWindow
        // продолжит показывать замороженный image после disconnect/failed reconnect.
        // В сочетании с RemoteScreenWindowShouldHide (MainWindow прячет окно) пользователь
        // видит чистое состояние, а при следующем connect overlay "Waiting for video stream..."
        // корректно отображается.
        _remoteFrameBitmap = null;
        RemoteFrameImage = null;
        _uiContext.Post(_ =>
        {
            RemoteCursor = Cursors.Arrow;
            ViewerAvailableDisplayIds.Clear();
            RefreshViewerDisplayIdsFallback();
            OnPropertyChanged(nameof(RemoteFrameImage));
            OnPropertyChanged(nameof(IsViewerAndConnected));
            OnPropertyChanged(nameof(RemoteDesktopButtonVisibility));
            ConnectionState = ConnectionState.Idle;
            // Спрятать Remote Desktop окно безусловно — event handler в MainWindow сам
            // проверит что окно было открыто (на host-стороне оно никогда не создавалось,
            // так что invoke ничего не сделает).
            RemoteScreenWindowShouldHide?.Invoke();
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
            var ok = await _sessionApiClient.CloseSessionAsync(ServerApiBaseUrl, sessionId, _currentOwnerSecret).ConfigureAwait(false);
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

    /// <summary>Single-flight guard: если dialog уже открыт (активно ждёт host'а), последующие offer'ы
    /// переиспользуют этот же Task вместо post'а нового диалога. Без этого guard'а несколько offer'ов
    /// (viewer retry / ICE restart / reconnect) стакают dialog'и на UI thread.</summary>
    private Task<bool>? _pendingViewerConfirmation;
    private readonly object _viewerConfirmationLock = new();

    /// <summary>True если на текущее подключение уже показали "Зритель подключён" (тост + зелёный статус).
    /// Peer_state, confirmation approve, password success, auto-approve — всё может триггерить
    /// уведомление, flag защищает от повторных тостов в рамках одной сессии. Сбрасывается
    /// в CleanupConnectionAsync для следующего viewer'а.</summary>
    private volatile bool _viewerConnectedNotified;

    /// <summary>
    /// Trigger финальное "Зритель подключён" уведомление — тост + зелёный статус.
    /// Idempotent: повторные вызовы игнорируются (flag _viewerConnectedNotified).
    /// Вызывается из трёх paths:
    ///   1. Confirmation dialog approved (host нажал "Разрешить").
    ///   2. Auto-approve (RequireConfirmation=false, без password gate).
    ///   3. Unattended password verified (UnattendedAuth.cs) — c optional
    ///      custom status ("Viewer подключён (пароль)", синий).
    /// </summary>
    internal void NotifyViewerFullyApproved(string? customStatus = null, Brush? customBrush = null)
    {
        if (_viewerConnectedNotified) return;
        _viewerConnectedNotified = true;
        // Defensive: если caller передал не-frozen brush (created на background thread),
        // freeze его здесь чтобы избежать cross-thread crash в DependencyProperty binding.
        // Frozen brush — immutable + thread-safe; .Freeze() no-op если уже frozen.
        if (customBrush is not null && customBrush.CanFreeze && !customBrush.IsFrozen)
            customBrush.Freeze();
        _uiContext.Post(_ =>
        {
            ViewerConnectionStatus = customStatus ?? Strings.Status_ViewerConnected;
            var greenBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            greenBrush.Freeze(); // safe для multi-thread (binding attach со всех threads)
            ViewerConnectionStatusBrush = customBrush ?? greenBrush;
            OnPropertyChanged(nameof(ViewerConnectionStatus));
            OnPropertyChanged(nameof(ViewerConnectionStatusBrush));
            ViewerConnectedNotification?.Invoke(); // тост в MainWindow.OnViewerConnected
        }, null);
    }

    /// <summary>NET-05: Show confirmation dialog on UI thread and wait for host's decision.
    /// Single-flight — concurrent calls возвращают тот же Task, не открывают повторные dialog'и.</summary>
    private Task<bool> ShowViewerConfirmationAsync()
    {
        lock (_viewerConfirmationLock)
        {
            var existing = _pendingViewerConfirmation;
            if (existing is not null && !existing.IsCompleted)
            {
                _logService.Debug("UiApp", "viewer_confirmation_deduplicated — reusing pending dialog");
                return existing;
            }
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        timeoutCts.Token.Register(() => tcs.TrySetResult(false));

        lock (_viewerConfirmationLock)
        {
            _pendingViewerConfirmation = tcs.Task;
        }

        _uiContext.Post(_ =>
        {
            try
            {
                System.Media.SystemSounds.Exclamation.Play();
                var dlg = new ConfirmDialog(
                    Strings.Dialog_ViewerApproval_Message,
                    Strings.Dialog_ViewerApproval_ConfirmButton);
                dlg.Owner = Application.Current.MainWindow;
                var result = dlg.ShowDialog() == true;
                tcs.TrySetResult(result);
            }
            catch
            {
                tcs.TrySetResult(false);
            }
            finally
            {
                timeoutCts.Dispose();
                lock (_viewerConfirmationLock)
                {
                    if (ReferenceEquals(_pendingViewerConfirmation, tcs.Task))
                        _pendingViewerConfirmation = null;
                }
            }
        }, null);

        return tcs.Task;
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
            catch (Exception ex) { allOk = false; if (attempt <= 3 || attempt % 10 == 0) _logService.Debug("Setup", $"dc_waiting_for_peer attempt={attempt}: {ex.Message}"); }

            try
            {
                if (shouldSendHostDisplays)
                { await SendHostDisplaysIfHostAsync(ct); shouldSendHostDisplays = false; }
            }
            catch (Exception ex) { allOk = false; if (attempt <= 3 || attempt % 10 == 0) _logService.Debug("Setup", $"dc_waiting_for_peer attempt={attempt}: {ex.Message}"); }

            try
            {
                if (shouldRequestHostDisplays)
                {
                    await dc.SendHostDisplaysRequestAsync(new HostDisplaysRequestPayload
                    { RequestId = Guid.NewGuid().ToString("N") }, ct);
                    shouldRequestHostDisplays = false;
                }
            }
            catch (Exception ex) { allOk = false; if (attempt <= 3 || attempt % 10 == 0) _logService.Debug("Setup", $"dc_waiting_for_peer attempt={attempt}: {ex.Message}"); }

            try
            {
                if (shouldSendClipboardInit)
                {
                    await dc.SendClipboardAsync(new ClipboardTextPayload
                    { Text = "zconect-init", OriginPeerId = "local" }, ct);
                    shouldSendClipboardInit = false;
                }
            }
            catch (Exception ex) { allOk = false; if (attempt <= 3 || attempt % 10 == 0) _logService.Debug("Setup", $"dc_waiting_for_peer attempt={attempt}: {ex.Message}"); }

            // Выходим только когда ВСЕ pending sends реально прошли (shouldXxx=false).
            // Раньше здесь было `allOk || ...` — но `allOk` остаётся true и при skip'е
            // (например screen_meta при _localScreenMeta==null), что приводит к преждевременному
            // выходу: host не дослылал screen_meta, viewer упирался в mouse_input_blocked.
            if (!shouldSendScreenMeta && !shouldSendHostDisplays && !shouldRequestHostDisplays && !shouldSendClipboardInit)
            {
                StartRttLoopIfNeeded();
                StartQualityStatsLoopIfNeeded();
                return;
            }

            // Если DC ещё не открыт — ждём event (до 1с). Если уже открыт — короткий polling,
            // пока подтянется _localScreenMeta (или другой ещё не готовый payload), без tight spin.
            if (!channelOpenTcs.Task.IsCompleted)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(1000);
                    await channelOpenTcs.Task.WaitAsync(cts.Token);
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
            else
            {
                try { await Task.Delay(250, ct); }
                catch (OperationCanceledException) { return; }
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
        // Restore Secure Desktop policy when viewer disconnects.
        RestoreSecureDesktopPolicy();

        // 1. Cancel background tasks (clipboard, cursor, etc.)
        try { _lifetimeCts.Cancel(); } catch { }

        // 2. Unsubscribe and dispose old WebRTC objects.
        try { _signalingCoordinator?.Detach(); } catch { }
        try { if (_signalingCoordinator is not null) _signalingCoordinator.IceCandidateObserved -= OnIceCandidateObserved; } catch { }
        _logService.Debug("Events", "signaling_events_unsubscribed");
        try
        {
            if (_realPeerAgent is not null)
            {
                _realPeerAgent.RemoteVideoFrameReceived -= OnRemoteVideoFrameReceived;
                _realPeerAgent.IceStateFailed -= OnIceStateFailed;
                _realPeerAgent.IceStateDisconnected -= OnIceStateDisconnected;
                _realPeerAgent.IceStateReconnected -= OnIceStateReconnected;
                _realPeerAgent.DesktopAccessLost -= OnDesktopAccessLost;
                _realPeerAgent.DesktopReinited -= OnDesktopReinited;
                _logService.Debug("Events", "peer_agent_events_unsubscribed");
            }
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
        Interlocked.Exchange(ref _callerStartedFlag, 0);
        _hostPeerStateAnnouncedBack = false;
        _hostVideoStarted = false;
        // CRITICAL: reset approval state for next viewer.
        // Без этого stale _viewerApproved=true от предыдущего viewer'а пропустит confirmation gate
        // и новый viewer получит full access без approval — security breach.
        _viewerApproved = false;
        // Вместе с _viewerApproved — также сбросить флаг что "уже уведомили о подключении".
        // Без этого NotifyViewerFullyApproved no-op'ится на повторном approve и статус
        // остаётся amber "ждёт одобрения" после того как host реально approve'нул.
        _viewerConnectedNotified = false;
        _pendingAuthNonce = null;
        _remoteScreenMeta = null;
        _localScreenMeta = null;
        _mouseInputBlockedWarnedNoMeta = false;
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
        _pendingDeleteRequestIds.Clear();
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
            _realPeerAgent = new MixedRealityPeerConnectionAgent(BuildTransportSettings(), msg => _logService.Debug("WebRTC", msg));
            var realDataAgent = new MixedRealityDataChannelAgent();
            _realPeerAgent.DataChannelAdded += ch => realDataAgent.AttachChannel(ch);
            await _realPeerAgent.InitializeAsync(_lifetimeCts.Token, includeVideoTransceiver: !_fileTransferOnly);
            peerAgent = _realPeerAgent;
            dataAgent = realDataAgent;
        }
        catch (Exception ex)
        {
            // N7-06: fail loudly instead of silent mock fallback — viewer would see nothing.
            _logService.Error("UiApp", "reset_webrtc_init_failed", ex.Message);
            SetStatus("Ошибка WebRTC при переподключении", ConnectionState.Error);
            return;
        }
        peerAgent.RemoteVideoFrameReceived += OnRemoteVideoFrameReceived;
        peerAgent.IceStateFailed += OnIceStateFailed;
        peerAgent.IceStateDisconnected += OnIceStateDisconnected;
        peerAgent.IceStateReconnected += OnIceStateReconnected;
        if (_realPeerAgent is not null)
        {
            _realPeerAgent.DesktopAccessLost += OnDesktopAccessLost;
            _realPeerAgent.DesktopReinited += OnDesktopReinited;
            _realPeerAgent.TrySwitchToActiveDesktop = SwitchCaptureThreadToActiveDesktop;
        }
        _logService.Debug("Events", "peer_agent_events_subscribed");

        // 5. Re-create SignalingCoordinator (reuses existing _signalingClient).
        _signalingCoordinator = new SignalingCoordinator(
            _signalingClient!,
            peerAgent,
            m => _logService.Debug("Signaling", m),
            beforeCreateAnswerAsync: async (offerSdp) =>
            {
                if (_role == ConnectionRole.Host)
                {
                    // NET-05: confirmation gate — ask host before accepting new viewer.
                    if (_settings.RequireConfirmation && !_viewerApproved)
                    {
                        var approved = await ShowViewerConfirmationAsync();
                        if (!approved)
                        {
                            _logService.Info("UiApp", "viewer_confirm_rejected");
                            // Notify viewer explicitly so его UI abort'ит сразу, не ждёт answer timeout.
                            try
                            {
                                await _signalingClient!.SendAsync("peer_state", _currentSessionId,
                                    new { state = "rejected" }, _lifetimeCts.Token);
                            }
                            catch { /* best effort — viewer упадёт по WS timeout если это не дошло */ }
                            throw new OperationCanceledException("viewer_rejected_by_host");
                        }
                        _viewerApproved = true;
                        _logService.Info("UiApp", "viewer_confirm_approved");
                    }
                    NotifyViewerFullyApproved(); // idempotent — triggers toast + green status
                }

                var offerHasVideo = offerSdp.Contains("m=video", StringComparison.OrdinalIgnoreCase);
                if (_role == ConnectionRole.Host && !_hostVideoStarted && _realPeerAgent is not null && offerHasVideo)
                {
                    _hostVideoStarted = true;
                    await ConfigureLocalVideoAsync(_realPeerAgent);
                    StartAutoQualityLoopIfNeeded();
                    StartRttLoopIfNeeded();
                    StartQualityStatsLoopIfNeeded();
                    _logService.Info("ScreenCapture", "local_video_started_on_offer_reset");
                }
            });
        _signalingCoordinator.IceCandidateObserved += OnIceCandidateObserved;
        _signalingCoordinator.SetSession(_currentSessionId);
        _logService.Debug("Events", "signaling_events_subscribed");

        // 6. Re-create DataChannelCoordinator.
        _isDataChannelReal = dataAgent is not MockDataChannelAgent;
        _dataChannelCoordinator = new DataChannelCoordinator(
            dataAgent,
            m => _logService.Debug("DataChannel", m));
        WireUpDataChannelCoordinator(ConnectionRole.Host);
        _logService.Debug("Events", "data_channel_wired");

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
            _logService.Warn("UiApp", $"peer_disconnected_received role={_role} viewer_was_connected={_hostVideoStarted || _hostPeerStateAnnouncedBack}");
            if (_role == ConnectionRole.Host)
            {
                // Ignore phantom peer_disconnected — server sends this when host's own
                // old WS connection is cleaned up after reconnect (e.g. VPN toggle).
                // Only reset if a viewer actually connected (SDP offer received or peer_state seen).
                if (!_hostVideoStarted && !_hostPeerStateAnnouncedBack)
                {
                    _logService.Info("UiApp", "peer_disconnected_ignored_no_viewer_was_connected");
                    return;
                }

                // Host stays connected to WS, resets WebRTC to accept new viewer.
                _ = Task.Run(async () =>
                {
                    await ResetForNewViewerAsync().ConfigureAwait(false);
                    _uiContext.Post(_ =>
                    {
                        SetStatus(Strings.Status_ViewerDisconnectedReconnecting, ConnectionState.Connecting);
                        ViewerConnectionStatus = Strings.Status_ViewerDisconnected;
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
            // Host explicitly rejected: abort viewer сразу, не ждать answer / DC timeout.
            if (_role == ConnectionRole.Viewer
                && msg.Payload.ValueKind == System.Text.Json.JsonValueKind.Object
                && msg.Payload.TryGetProperty("state", out var stateProp)
                && stateProp.ValueKind == System.Text.Json.JsonValueKind.String
                && stateProp.GetString() == "rejected")
            {
                _logService.Warn("UiApp", "host_rejected_connection");
                _uiContext.Post(_ =>
                {
                    SetStatus("Хост отклонил подключение", ConnectionState.Error);
                    OnPropertyChanged(nameof(StatusText));
                }, null);
                _ = Task.Run(async () =>
                {
                    try { await CleanupConnectionAsync().ConfigureAwait(false); }
                    catch { /* best effort */ }
                });
                return;
            }

            // Host may have connected before viewer; re-announce host state when we notice the other side joined.
            if (_role == ConnectionRole.Host && !_hostPeerStateAnnouncedBack && _signalingClient is not null)
            {
                _hostPeerStateAnnouncedBack = true;
                // Disable Secure Desktop so UAC shows on Default desktop (DXGI can capture).
                DisableSecureDesktopForRemote();
                // Pending-state UI: viewer joined signaling, но ещё НЕ прошёл
                // confirmation gate / password auth / offer-answer exchange.
                // Показываем amber "ждёт/подключается", тост НЕ даём — он пойдёт
                // после успешного approve (NotifyViewerFullyApproved).
                _uiContext.Post(_ =>
                {
                    bool needsConfirmation = _settings.RequireConfirmation && !_viewerApproved;
                    ViewerConnectionStatus = needsConfirmation
                        ? "Зритель ждёт одобрения..."
                        : "Зритель подключается...";
                    ViewerConnectionStatusBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // amber
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
            if (_callerStartDeferred && Interlocked.CompareExchange(ref _callerStartedFlag, 1, 0) == 0 && _signalingCoordinator is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _signalingCoordinator.StartAsCallerAsync(_currentSessionId, _lifetimeCts.Token);
                        Interlocked.Exchange(ref _callerStartedFlag, 1);
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

    // ── UAC: desktop switch for capture + input ──────────────────────

    /// <summary>
    /// True when secure desktop (Winlogon/UAC) is active. Источник — pipe
    /// notifications от service's DesktopMonitor (Stage 3). UI под user
    /// token не мог сам детектить Winlogon (нет permission).
    /// </summary>
    public bool IsSecureDesktopActive =>
        !string.IsNullOrEmpty(_cachedActiveDesktop)
        && !_cachedActiveDesktop.Equals("Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>Called after capture reinit on a new desktop — force cursor resync.</summary>
    private void OnDesktopReinited(string newDesktop)
    {
        _lastSentCursorType = string.Empty;
        _logService.Debug("UiApp", $"desktop_reinited_{newDesktop}_cursor_resync");
    }

    /// <summary>Called when DXGI capture loses access to the desktop.</summary>
    private void OnDesktopAccessLost()
    {
        _logService.Warn("UiApp", "desktop_access_lost_secure_desktop_active");
    }

    /// <summary>Called by DesktopMonitor on ANY desktop switch (both Default→Winlogon and Winlogon→Default).</summary>
    private void OnDesktopSwitched(string oldDesktop, string newDesktop)
    {
        _logService.Info("UiApp", $"desktop_switched from={oldDesktop} to={newDesktop}");

        // Force cursor resync — after desktop switch cursor state is stale.
        _lastSentCursorType = string.Empty;

        var isDefault = newDesktop.Equals("Default", StringComparison.OrdinalIgnoreCase);
        if (isDefault && _realPeerAgent is not null)
        {
            _realPeerAgent.ForceDesktopAccessLost();
            _logService.Info("UiApp", "desktop_default_forcing_dxgi_reinit");
        }
        else
        {
            _realPeerAgent?.ForceDesktopAccessLost();
        }
    }

    /// <summary>
    /// Handler подписан на pipe notifications (Stage 3). Service в session 0 под
    /// SYSTEM polls OpenInputDesktop каждые 500ms и шлёт сюда при смене desktop.
    /// UI кэширует значение для TigerVNC capture reinit check.
    /// </summary>
    private void OnServiceDesktopChanged(string newDesktop)
    {
        var old = _cachedActiveDesktop;
        _cachedActiveDesktop = newDesktop;
        _logService.Info("UiApp", $"desktop_changed_from_service from={old} to={newDesktop}");
        // Reuse existing handler — он знает что делать (capture reinit logic и т.д.).
        OnDesktopSwitched(old, newDesktop);
    }

    /// <summary>
    /// Wire the static GetCurrentDesktopName callback for DXGI capture reinit check.
    /// После Stage 3 возвращаем cached значение (обновляется pipe notifications от
    /// service). Старый путь (Services.DesktopMonitor.GetInputDesktopName) не мог
    /// видеть Winlogon — user token не имеет access на secure desktop.
    /// </summary>
    private void WireDesktopNameCallback()
    {
        WebRtcTransport.MixedRealityPeerConnectionAgent.GetCurrentDesktopName =
            () => _cachedActiveDesktop;
    }

    /// <summary>
    /// Called ON the capture thread when DXGI fails with AccessLost.
    /// Switches the capture thread to the current active desktop (Default or Winlogon).
    /// Requires SYSTEM token (process launched by service with --from-service).
    /// </summary>
    private static bool SwitchCaptureThreadToActiveDesktop()
    {
        var hDesk = DesktopInterop.OpenInputDesktop(0, false, DesktopInterop.GENERIC_ALL);
        if (hDesk == IntPtr.Zero) return false;

        if (!DesktopInterop.SetThreadDesktop(hDesk))
        {
            DesktopInterop.CloseDesktop(hDesk);
            return false;
        }

        // F-14 (external audit 2026-04-18): close previous handle перед assign new.
        // SetThreadDesktop keeps ссылку на new handle — закрыть его нельзя пока
        // thread на нём. Но PREVIOUS handle (от прошлого switch) остался owned
        // by нами и после переключения thread'а на новый desktop — previous
        // больше не используется. Без close на длинных uptime'ах + частых
        // UAC/lock switches handle accumulates.
        // Mirror helper's EnsureCurrentDesktop pattern (Program.cs:343-346).
        var prev = Interlocked.Exchange(ref _lastCaptureDesktopHandle, hDesk);
        if (prev != IntPtr.Zero && prev != hDesk)
            DesktopInterop.CloseDesktop(prev);
        return true;
    }

    /// <summary>Tracks previous desktop handle from SwitchCaptureThreadToActiveDesktop.
    /// Used to close old handle when switching to new desktop (F-14 leak fix).</summary>
    private static IntPtr _lastCaptureDesktopHandle = IntPtr.Zero;

    /// <summary>Called when ICE transitions to Disconnected — may recover on its own
    /// (brief WiFi hiccup, VPN toggle, adapter flap) or escalate to Failed.
    /// Show transient banner. On Viewer, proactively trigger ICE restart to speed recovery.</summary>
    private void OnIceStateDisconnected()
    {
        _logService.Warn("UiApp", $"ice_state_disconnected role={_role}");
        _iceWasDisconnectedRecently = true;

        _uiContext.Post(_ =>
        {
            if (_role == ConnectionRole.Host)
            {
                ViewerConnectionStatus = "Соединение нестабильно — восстанавливаем...";
                ViewerConnectionStatusBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            }
            else
            {
                SetStatus("Соединение нестабильно — восстанавливаем...", ConnectionState.Connecting);
            }
        }, null);

        // Viewer is the caller in our signaling model, so only it can issue an ICE restart offer.
        // Don't block the state-change callback thread — fire-and-forget the restart attempt.
        // Throttle: at most one restart per IceRestartMinIntervalMs + one in-flight — prevents
        // flood on flaky networks where Disconnected fires repeatedly.
        if (_role == ConnectionRole.Viewer && _signalingCoordinator is not null && !string.IsNullOrEmpty(_currentSessionId))
        {
            if (Interlocked.CompareExchange(ref _iceRestartInFlight, 1, 0) != 0)
            {
                _logService.Debug("UiApp", "ice_restart_skipped_in_flight");
                return;
            }
            var sinceLastMs = Environment.TickCount - Volatile.Read(ref _iceLastRestartTick);
            if (sinceLastMs >= 0 && sinceLastMs < IceRestartMinIntervalMs)
            {
                Interlocked.Exchange(ref _iceRestartInFlight, 0);
                _logService.Debug("UiApp", $"ice_restart_throttled_since={sinceLastMs}ms");
                return;
            }

            var sessionId = _currentSessionId;
            var coord = _signalingCoordinator;
            var ct = _lifetimeCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    _localIceCandidateType = "unknown";
                    _remoteIceCandidateType = "unknown";
                    coord.SetIceCandidateFilters(null, null);
                    await coord.StartIceRestartAsCallerAsync(sessionId, ct);
                    Volatile.Write(ref _iceLastRestartTick, Environment.TickCount);
                    _logService.Info("UiApp", "ice_restart_triggered_on_disconnected");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logService.Warn("UiApp", "ice_restart_on_disconnected_failed_" + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _iceRestartInFlight, 0);
                }
            });
        }
    }

    // ── ICE restart throttling ──
    /// <summary>Minimum interval between ICE restart attempts triggered by Disconnected events.
    /// Flaky networks can toggle Disconnected/Connected rapidly; restart flooding destabilizes
    /// recovery (each restart re-gathers candidates which briefly drops the connection).</summary>
    private const int IceRestartMinIntervalMs = 10_000;
    private int _iceRestartInFlight;
    private int _iceLastRestartTick = unchecked(Environment.TickCount - IceRestartMinIntervalMs);

    /// <summary>Called when ICE recovers to Connected/Completed after a Disconnected period.</summary>
    private void OnIceStateReconnected()
    {
        _logService.Info("UiApp", $"ice_state_reconnected role={_role}");
        _iceWasDisconnectedRecently = false;
        // Invalidate any pending grace timer from a prior Failed — we're back online.
        Interlocked.Increment(ref _iceFailedGraceToken);

        _uiContext.Post(_ =>
        {
            if (_role == ConnectionRole.Host)
            {
                ViewerConnectionStatus = Strings.Status_ViewerConnected;
                ViewerConnectionStatusBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            }
            else
            {
                var route = InferRouteType(_localIceCandidateType, _remoteIceCandidateType);
                var label = IceRouteDisplayLabel(route, _localIceCandidateIp, _remoteIceCandidateIp);
                SetStatus($"Подключено: {label}", ConnectionState.Connected);
            }
        }, null);
    }

    /// <summary>Monotonically-increasing token used to cancel in-flight teardown grace waits
    /// when ICE recovers (Reconnected fires between Failed and the grace timer elapsing).</summary>
    private int _iceFailedGraceToken;

    /// <summary>Called when WebRTC ICE state transitions to Failed — P2P is dead.
    /// Host waits a grace period before teardown: Viewer (caller in our signaling)
    /// may issue an ICE restart offer that brings the peer back to Connected without a full reset.</summary>
    private void OnIceStateFailed()
    {
        _logService.Warn("UiApp", $"ice_state_failed role={_role}");

        if (_role == ConnectionRole.Host)
        {
            var token = Interlocked.Increment(ref _iceFailedGraceToken);
            _uiContext.Post(_ =>
            {
                ViewerConnectionStatus = "Соединение нестабильно — восстанавливаем...";
                ViewerConnectionStatusBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            }, null);

            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(8), _lifetimeCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                if (Volatile.Read(ref _iceFailedGraceToken) != token)
                    return;
                if (!_iceWasDisconnectedRecently)
                    return;

                _logService.Warn("UiApp", "ice_failed_grace_expired_tearing_down");
                await ResetForNewViewerAsync().ConfigureAwait(false);
                _uiContext.Post(_ =>
                {
                    SetStatus(Strings.Status_ConnectionLostReconnecting, ConnectionState.Connecting);
                    ViewerConnectionStatus = "Зритель отключился (ICE)";
                    ViewerConnectionStatusBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                }, null);
            });
        }
        // Viewer: ICE failed is handled by health check → WS reconnect loop. No action needed here.
    }

    /// <summary>Set to true in OnIceStateDisconnected, cleared in OnIceStateReconnected.
    /// Host teardown grace uses this to abort if ICE recovered (no longer Failed).</summary>
    private volatile bool _iceWasDisconnectedRecently;

    private void OnSignalingDisconnected()
    {
        _logService.Warn("UiApp", "ws_disconnected");

        if ((_role != ConnectionRole.Host && _role != ConnectionRole.Viewer) || string.IsNullOrEmpty(_currentSessionId))
        {
            _uiContext.Post(_ => SetStatus("WS отключен", ConnectionState.Error), null);
            return;
        }

        // Don't start a second reconnect loop if one is already running
        // (e.g. viewer rejoin calls ConnectWsAsync which creates new WS → disconnect fires from old WS).
        if (_wsReconnectionManager.IsReconnecting)
        {
            _logService.Debug("UiApp", "ws_disconnected_ignored_reconnect_in_progress");
            return;
        }

        _uiContext.Post(_ => SetStatus("WS отключен, переподключение...", ConnectionState.Connecting), null);

        _ = Task.Run(async () =>
        {
            try { await WsReconnectLoopAsync(); }
            catch { /* ignore */ }
        });
    }

    /// <summary>Check if the P2P WebRTC connection is still alive (recent activity within threshold).</summary>
    private bool IsP2PAlive => _wsReconnectionManager.IsP2PAlive;

    /// <summary>Mark P2P as active (call on successful video frame or DC message).</summary>
    private void TouchP2PActivity() => _wsReconnectionManager.TouchP2PActivity();

    private async Task WsReconnectLoopAsync()
    {
        // Capture state before entering the manager (it only knows about callbacks).
        var wsUrl = _currentWsUrl;
        var sessionId = _currentSessionId;
        var ownerSecret = _currentOwnerSecret;
        var viewerLogin = _viewerJoinLoginCode;
        var viewerPass = _viewerJoinPassCode;
        var reconnectFtOnly = _fileTransferOnly;

        // UF-02 fix: use lifetime token to cancel reconnect on manual disconnect.
        var ct = _lifetimeCts.Token;

        // Store fresh token from host refresh for the WS reconnect callback.
        string? freshToken = null;

        var result = await _wsReconnectionManager.ReconnectAsync(
            onHostRefreshAsync: async (sid, secret, attempt) =>
            {
                var detailed = await _sessionApiClient.RefreshSessionAsyncDetailed(ServerApiBaseUrl, sid, secret);
                switch (detailed.Status)
                {
                    case SessionClient.RefreshStatus.Success:
                        freshToken = detailed.Response?.WsToken ?? string.Empty;
                        return string.IsNullOrEmpty(freshToken)
                            ? Services.WsReconnectionManager.HostRefreshOutcome.ServerError
                            : Services.WsReconnectionManager.HostRefreshOutcome.Success;
                    case SessionClient.RefreshStatus.SessionGone:
                        _logService.Info("UiApp", $"refresh_session_gone_http{detailed.HttpStatus}_immediate_recreate");
                        return Services.WsReconnectionManager.HostRefreshOutcome.SessionGone;
                    case SessionClient.RefreshStatus.NetworkError:
                        return Services.WsReconnectionManager.HostRefreshOutcome.NetworkError;
                    default:
                        return Services.WsReconnectionManager.HostRefreshOutcome.ServerError;
                }
            },
            onViewerRejoinAsync: async () =>
            {
                _logService.Debug("UiApp", $"viewer_rejoin login=****{viewerLogin[^4..]}");
                var joinResp = await _sessionApiClient.JoinSessionAsync(ServerApiBaseUrl, viewerLogin, viewerPass);
                if (joinResp is null)
                {
                    _logService.Warn("UiApp", "viewer_rejoin_failed_null_response");
                    return false;
                }
                // Rotating TURN: fresh creds из rejoin response. Writes здесь race'ятся с
                // reads в BuildTransportSettings (вызывается при новом PeerConnection);
                // atomic reference swap на x86/ARM + snapshot pattern in reader делает
                // это safe. См. BuildTransportSettings — берём local copy ссылки.
                if (joinResp.TurnServers is not null && joinResp.TurnServers.Count > 0)
                {
                    _dynamicTurnServers = joinResp.TurnServers;
                    _logService.Info("UiApp", $"turn_creds_refreshed_rejoin ttl={joinResp.TurnServers[0].TtlSeconds}s");
                    _uiContext.Post(_ => RefreshTurnStatusBindings(), null);
                }
                var viewerWsUrl = ResolveWsUrl(ServerApiBaseUrl, joinResp.WsUrl);
                var wsOk = await ConnectWsAsync(viewerWsUrl, joinResp.WsToken, joinResp.SessionId, startAsCaller: true, role: ConnectionRole.Viewer, fileTransferOnly: reconnectFtOnly);
                if (!wsOk)
                {
                    _logService.Warn("UiApp", "viewer_rejoin_ws_connect_failed");
                    return false;
                }
                if (!reconnectFtOnly)
                {
                    _uiContext.Post(_ => RemoteScreenWindowRequested?.Invoke(), null);
                }
                _uiContext.Post(_ => SetStatus("Переподключено", ConnectionState.Connected), null);
                return true;
            },
            onHostWsReconnectAsync: async (url, sid, secret) =>
            {
                if (string.IsNullOrEmpty(freshToken))
                {
                    _logService.Warn("UiApp", "ws_reconnect_fresh_token_null — onHostRefreshAsync не установил freshToken (callback missing или refresh failed)");
                    return false;
                }

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
                await newWs.ConnectAsync(url, sid, freshToken, CancellationToken.None);
                _signalingClient = newWs;
                _currentWsToken = freshToken;

                if (_signalingCoordinator is not null)
                {
                    _signalingCoordinator.ReplaceSignalingClient(newWs);
                }
                // Bug fix 2026-04-27: UnattendedAuthCoord держит reference на старый ws
                // (disposed). Без replace probe-handler chain ломается → host больше
                // не отвечает на viewer probe → confirmation dialog каждый раз.
                _unattendedAuthCoord?.ReplaceSignalingClient(newWs);

                try
                {
                    await newWs.SendAsync("peer_state", sid, new { state = "host_ready" }, CancellationToken.None);
                }
                catch { /* best effort */ }

                _uiContext.Post(_ => SetStatus("WS переподключен", ConnectionState.Connected), null);
                return true;
            },
            onStatusUpdate: (msg, p2pAlive) =>
            {
                _uiContext.Post(_ => SetStatus(msg, p2pAlive ? ConnectionState.Connected : ConnectionState.Connecting), null);
            },
            wsUrl: wsUrl,
            sessionId: sessionId,
            ownerSecret: ownerSecret,
            ct: ct);

        // Handle post-loop results.
        switch (result)
        {
            case Services.WsReconnectionManager.ReconnectResult.HostNeedsRecreate:
                // Cleanup dead WS client ПЕРЕД CreateSessionAsync — иначе его early-return
                // "Сессия уже создана" блокирует recreate (видя _signalingClient != null).
                // User жаловался что host "стоит с ошибкой" пока service не перезапустишь.
                _logService.Info("UiApp", "host_auto_recreate_after_ws_failure");
                _uiContext.Post(async _ =>
                {
                    try { await CleanupConnectionAsync().ConfigureAwait(false); }
                    catch (Exception ex) { _logService.Warn("UiApp", $"cleanup_before_recreate_failed: {ex.Message}"); }
                    SetStatus("Сервер был недоступен, создаём новую сессию...", ConnectionState.Connecting);
                    _ = CreateSessionAsync();
                }, null);
                break;
            case Services.WsReconnectionManager.ReconnectResult.GaveUpP2PAlive:
                _logService.Info("UiApp", "ws_offline_but_p2p_alive");
                _uiContext.Post(_ => SetStatus("P2P активен (сигнализация offline)", ConnectionState.Connected), null);
                break;
            case Services.WsReconnectionManager.ReconnectResult.GaveUpP2PDead:
                _uiContext.Post(_ => SetStatus("Не удалось переподключить WS", ConnectionState.Error), null);
                break;
        }
    }

    // ── Server-side rejection handling (ban / maintenance / locked / blocked) ──
    // Показывает user-facing сообщение через SetStatus (status bar) + MessageBox
    // для критичных ban/maintenance (нельзя просто status bar — user может не заметить).
    // Локализовано через Strings.* — работает в обеих локалях.

    private void HandleCreateSessionFailure(CreateSessionResult r)
    {
        switch (r.Status)
        {
            case CreateSessionStatus.Banned:
                _logService.Warn("UiApp", $"create_session_banned http={r.HttpStatus} msg={r.ErrorMessage}");
                SetStatus(Strings.Status_Banned, ConnectionState.Error);
                ShowBlockedDialog(Strings.Dialog_Title_Blocked, Strings.Status_Banned);
                break;
            case CreateSessionStatus.Maintenance:
                _logService.Info("UiApp", "create_session_maintenance");
                SetStatus(Strings.Status_Maintenance, ConnectionState.Error);
                ShowBlockedDialog(Strings.Dialog_Title_Maintenance, Strings.Status_Maintenance);
                break;
            case CreateSessionStatus.NetworkError:
                _logService.Warn("UiApp", $"create_session_network_error: {r.ErrorMessage}");
                SetStatus(Strings.Status_NetworkError, ConnectionState.Error);
                break;
            case CreateSessionStatus.ServerError:
                _logService.Warn("UiApp", $"create_session_server_error http={r.HttpStatus}");
                SetStatus(Strings.Status_ServerError, ConnectionState.Error);
                break;
            default:
                _logService.Warn("UiApp", $"create_session_failed status={r.Status} http={r.HttpStatus}");
                SetStatus(Strings.Status_CreateSessionError, ConnectionState.Error);
                break;
        }
    }

    private void HandleJoinFailure(JoinSessionResult r, string loginCodeMasked)
    {
        switch (r.Status)
        {
            case JoinSessionStatus.Banned:
                _logService.Warn("UiApp", $"join_banned http={r.HttpStatus}");
                SetStatus(Strings.Status_Banned, ConnectionState.Error);
                ShowBlockedDialog(Strings.Dialog_Title_Blocked, Strings.Status_Banned);
                break;
            case JoinSessionStatus.SessionBlocked:
                _logService.Warn("UiApp", "join_session_blocked_permanent");
                SetStatus(Strings.Status_SessionBlocked, ConnectionState.Error);
                ShowBlockedDialog(Strings.Dialog_Title_Blocked, Strings.Status_SessionBlocked);
                break;
            case JoinSessionStatus.SessionLocked:
                _logService.Warn("UiApp", $"join_session_locked retry_after_sec={r.RetryAfterSec}");
                // Если server сказал сколько ждать — показываем countdown. Иначе generic.
                var msg = r.RetryAfterSec > 0
                    ? string.Format(Strings.Status_SessionLocked_Format, r.RetryAfterSec)
                    : Strings.Status_SessionLocked_NoTime;
                SetStatus(msg, ConnectionState.Error);
                // MessageBox для locked — избыточно, 429 с Retry-After достаточно в status bar.
                // User увидит и сам попробует позже.
                break;
            case JoinSessionStatus.InvalidCredentials:
                // Уже generic message от сервера; не делаем MessageBox чтобы не надоедать
                // при typos. Status bar достаточно.
                _logService.Info("UiApp", "join_invalid_credentials");
                SetStatus(Strings.Status_InvalidCredentials, ConnectionState.Error);
                break;
            case JoinSessionStatus.NetworkError:
                _logService.Warn("UiApp", $"join_network_error: {r.ErrorMessage}");
                SetStatus(Strings.Status_NetworkError, ConnectionState.Error);
                break;
            case JoinSessionStatus.ServerError:
                _logService.Warn("UiApp", $"join_server_error http={r.HttpStatus}");
                SetStatus(Strings.Status_ServerError, ConnectionState.Error);
                break;
            default:
                _logService.Warn("UiApp", $"join_failed status={r.Status} http={r.HttpStatus}");
                SetStatus(Strings.Status_ConnectError, ConnectionState.Error);
                break;
        }
    }

    /// <summary>MessageBox для критичных user-facing rejections (ban / permanent block / maintenance).
    /// Dispatcher.Invoke safe — MessageBox требует UI thread.</summary>
    private void ShowBlockedDialog(string title, string message)
    {
        try
        {
            _uiContext.Post(_ =>
            {
                var owner = Application.Current?.MainWindow;
                if (owner is not null)
                    MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }, null);
        }
        catch { /* UI teardown — ignore */ }
    }
}

