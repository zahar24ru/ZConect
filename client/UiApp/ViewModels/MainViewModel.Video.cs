using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using QualityController;
using ScreenCapture;
using UiApp.Properties;
using WebRtcTransport;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel
{
    // ── Video stats (measured on viewer side) ──
    private int _videoStatsFrameCount;
    private long _videoStatsByteCount;
    private readonly Stopwatch _videoStatsStopwatch = new();
    private string _videoStatsText = string.Empty;
    public string VideoStatsText
    {
        get => _videoStatsText;
        private set { _videoStatsText = value; OnPropertyChanged(); }
    }

    private void UpdateVideoStats(RemoteVideoFrame frame)
    {
        if (!_videoStatsStopwatch.IsRunning)
            _videoStatsStopwatch.Start();

        Interlocked.Increment(ref _videoStatsFrameCount);
        Interlocked.Add(ref _videoStatsByteCount, frame.Buffer.Length);

        var elapsed = _videoStatsStopwatch.ElapsedMilliseconds;
        if (elapsed < 1000) return;

        var frames = Interlocked.Exchange(ref _videoStatsFrameCount, 0);
        var bytes = Interlocked.Exchange(ref _videoStatsByteCount, 0);
        _videoStatsStopwatch.Restart();

        var fps = frames * 1000.0 / elapsed;
        var kbps = bytes * 8.0 / elapsed; // bytes per ms = kbps
        var resolution = $"{frame.Width}x{frame.Height}";

        var rttSuffix = _rttMs >= 0 ? $"  RTT: {_rttMs:F0}ms" : "";
        _uiContext.Post(_ =>
        {
            VideoStatsText = $"{resolution}  {fps:F0} fps  {FormatBitrate(kbps)}{rttSuffix}";
        }, null);
    }

    private static string FormatBitrate(double kbps)
    {
        if (kbps >= 1000)
            return $"{kbps / 1000:F1} Mbps";
        return $"{kbps:F0} kbps";
    }

    private void ResetVideoStats()
    {
        _videoStatsFrameCount = 0;
        _videoStatsByteCount = 0;
        _videoStatsStopwatch.Reset();
        VideoStatsText = string.Empty;
        _hostStatsStopwatch.Reset();
        _rttMs = -1;
        RttText = string.Empty;
        IceDetailsText = string.Empty;
        _prevRxBytes = 0;
        _prevRxFrames = 0;
        _prevRxDropped = 0;
        _prevRxTimestampUs = 0;
        Interlocked.Exchange(ref _healthCheckTriggered, 0);
    }

    // ── RTT measurement via data channel ping/pong + health check ──
    private readonly Stopwatch _rttStopwatch = Stopwatch.StartNew();
    private long _lastPingSentMs;
    private long _lastPongReceivedMs;
    private double _rttMs = -1;
    private int _rttLoopStarted;
    private const int HealthCheckTimeoutMs = 30_000; // 30s without pong → reconnect
    private int _healthCheckTriggered;

    private string _rttText = string.Empty;
    public string RttText
    {
        get => _rttText;
        private set { _rttText = value; OnPropertyChanged(); }
    }

    private void OnPingReceived(PingPayload payload)
    {
        // Respond with pong immediately
        if (_dataChannelCoordinator is not null)
            _ = Task.Run(async () =>
            {
                try { await _dataChannelCoordinator.SendPongAsync(new PongPayload { TimestampMs = payload.TimestampMs }, _lifetimeCts.Token); }
                catch { /* ignore — connection may be closing */ }
            });
    }

    private void OnPongReceived(PongPayload payload)
    {
        var now = _rttStopwatch.ElapsedMilliseconds;
        Interlocked.Exchange(ref _lastPongReceivedMs, now);
        var rtt = now - payload.TimestampMs;
        if (rtt >= 0 && rtt < 10000) // sanity check
        {
            // Exponential moving average for smooth display
            _rttMs = _rttMs < 0 ? rtt : _rttMs * 0.7 + rtt * 0.3;
            _uiContext.Post(_ =>
            {
                RttText = $"RTT: {_rttMs:F0} ms";
            }, null);
        }
    }

    private void StartRttLoopIfNeeded()
    {
        if (Interlocked.CompareExchange(ref _rttLoopStarted, 1, 0) != 0) return;
        Interlocked.Exchange(ref _lastPongReceivedMs, _rttStopwatch.ElapsedMilliseconds);
        Interlocked.Exchange(ref _healthCheckTriggered, 0);
        _ = Task.Run(() => RttPingLoopAsync(_lifetimeCts.Token));
    }

    private async Task RttPingLoopAsync(CancellationToken ct)
    {
        // Wait a bit for connection to establish
        await Task.Delay(2000, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_dataChannelCoordinator is not null)
                {
                    _lastPingSentMs = _rttStopwatch.ElapsedMilliseconds;
                    await _dataChannelCoordinator.SendPingAsync(
                        new PingPayload { TimestampMs = _lastPingSentMs }, ct);

                    // Health check: if no pong received for 30s, trigger reconnect.
                    var lastPong = Interlocked.Read(ref _lastPongReceivedMs);
                    var silenceMs = _rttStopwatch.ElapsedMilliseconds - lastPong;
                    if (silenceMs > HealthCheckTimeoutMs && Interlocked.CompareExchange(ref _healthCheckTriggered, 1, 0) == 0)
                    {
                        _logService.Warn("HealthCheck", $"no_pong_for_{silenceMs}ms_triggering_reconnect");
                        _uiContext.Post(_ => SetStatus(Strings.Status_ConnectionLostShort, Models.ConnectionState.Connecting), null);
                        _ = Task.Run(async () =>
                        {
                            try { await WsReconnectLoopAsync(); }
                            catch { /* ignore */ }
                        });
                        return; // stop this ping loop — reconnect will start a new one
                    }
                }
                await Task.Delay(3000, ct); // ping every 3 seconds
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore transient failures */ }
        }
    }

    // ── Viewer-side ICE/media quality overlay (toggle via Ctrl+Shift+I) ──
    private string _iceDetailsText = string.Empty;
    /// <summary>Detailed ICE/media diagnostic overlay line. Shown only when ShowIceDetailsOverlay=true.
    /// Computed from VideoReceiverStats delta: bitrate, frames dropped %. Merged with ICE route/TURN info.</summary>
    public string IceDetailsText
    {
        get => _iceDetailsText;
        private set { _iceDetailsText = value; OnPropertyChanged(); }
    }

    private bool _showIceDetailsOverlay;
    /// <summary>When true, extra overlay line shows IceDetailsText (route type, TURN info)
    /// below VideoStatsText. Toggle via Ctrl+Shift+I on RemoteScreenWindow.</summary>
    public bool ShowIceDetailsOverlay
    {
        get => _showIceDetailsOverlay;
        set { if (_showIceDetailsOverlay == value) return; _showIceDetailsOverlay = value; OnPropertyChanged(); }
    }

    /// <summary>Viewer quality overlay toggle (Ctrl+I на RemoteScreenWindow).
    /// Скрывает/показывает FPS/bitrate/RTT pill. Persist'ится в ClientSettings.</summary>
    public bool ShowQualityOverlay
    {
        get => _settings.ShowQualityOverlay;
        set
        {
            if (_settings.ShowQualityOverlay == value) return;
            _settings.ShowQualityOverlay = value;
            _settingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    private int _qualityStatsLoopStarted;
    private long _prevRxBytes;
    private long _prevRxFrames;
    private long _prevRxDropped;
    private long _prevRxTimestampUs;

    private void StartQualityStatsLoopIfNeeded()
    {
        if (_role != ConnectionRole.Viewer) return;
        if (Interlocked.CompareExchange(ref _qualityStatsLoopStarted, 1, 0) != 0) return;
        _prevRxBytes = 0;
        _prevRxFrames = 0;
        _prevRxDropped = 0;
        _prevRxTimestampUs = 0;
        _ = Task.Run(() => QualityStatsLoopAsync(_lifetimeCts.Token));
    }

    private async Task QualityStatsLoopAsync(CancellationToken ct)
    {
        await Task.Delay(2000, ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1500, ct);
                if (_realPeerAgent is null) continue;

                using var stats = await _realPeerAgent.PeerConnection.GetSimpleStatsAsync();
                var vr = stats.GetStats<Microsoft.MixedReality.WebRTC.PeerConnection.VideoReceiverStats>().FirstOrDefault();
                if (vr.RtpStatsTimestampUs == 0) continue;

                double mbps = 0;
                double dropPct = 0;
                var currentTs = (long)vr.RtpStatsTimestampUs;
                var currentBytes = (long)vr.BytesReceived;
                var currentFrames = (long)vr.FramesReceived;
                var currentDropped = (long)vr.FramesDropped;
                if (_prevRxTimestampUs != 0 && currentTs > _prevRxTimestampUs)
                {
                    var deltaUs = currentTs - _prevRxTimestampUs;
                    var deltaBytes = currentBytes - _prevRxBytes;
                    mbps = deltaBytes * 8.0 / deltaUs; // bytes*8/us = Mbps
                    var deltaFrames = currentFrames - _prevRxFrames;
                    var deltaDropped = currentDropped - _prevRxDropped;
                    if (deltaFrames > 0)
                        dropPct = 100.0 * deltaDropped / deltaFrames;
                }
                _prevRxBytes = currentBytes;
                _prevRxFrames = currentFrames;
                _prevRxDropped = currentDropped;
                _prevRxTimestampUs = currentTs;

                var route = InferRouteType(_localIceCandidateType, _remoteIceCandidateType);
                var routeLabel = IceRouteDisplayLabel(route, _localIceCandidateIp, _remoteIceCandidateIp);
                var rtt = _rttMs >= 0 ? $"RTT {_rttMs:F0}ms" : "RTT —";
                var rate = mbps >= 1 ? $"{mbps:F1} Mbps" : $"{mbps * 1000:F0} kbps";
                var drops = dropPct > 0.05 ? $"  drop {dropPct:F1}%" : "";
                var line1 = $"{rate}  {rtt}{drops}";
                var line2 = $"{routeLabel}";

                _uiContext.Post(_ =>
                {
                    IceDetailsText = $"{line1}\n{line2}";
                }, null);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logService.Debug("QualityStats", "loop_error_" + ex.Message);
            }
        }
        Interlocked.Exchange(ref _qualityStatsLoopStarted, 0);
    }

    // ── Host-side capture stats (skip %) ──
    private readonly Stopwatch _hostStatsStopwatch = new();
    private string _hostStatsText = string.Empty;
    public string HostStatsText
    {
        get => _hostStatsText;
        private set { _hostStatsText = value; OnPropertyChanged(); }
    }

    private void UpdateHostCaptureStats()
    {
        if (_role != ConnectionRole.Host || _realPeerAgent is null) return;

        if (!_hostStatsStopwatch.IsRunning)
            _hostStatsStopwatch.Start();

        if (_hostStatsStopwatch.ElapsedMilliseconds < 1000) return;
        _hostStatsStopwatch.Restart();

        var (skipped, total) = _realPeerAgent.ConsumeFrameSkipStats();
        if (total == 0) return;

        var skipPct = 100.0 * skipped / total;
        var sent = total - skipped;
        var rttSuffix = _rttMs >= 0 ? $"  RTT: {_rttMs:F0}ms" : "";
        _uiContext.Post(_ =>
        {
            HostStatsText = string.Format(Strings.HostStats_Format, total, sent, skipPct, rttSuffix);
        }, null);
    }

    // ── Auto-quality adaptation (host-side) ──
    private AutoQualityAdapter? _autoQualityAdapter;
    private int _autoQualityLoopStarted;

    private void StartAutoQualityLoopIfNeeded()
    {
        if (_role != ConnectionRole.Host) return;
        if (!string.Equals(QualityPreset?.Trim(), "Auto", StringComparison.OrdinalIgnoreCase)) return;
        if (Interlocked.CompareExchange(ref _autoQualityLoopStarted, 1, 0) != 0) return;

        _autoQualityAdapter = new AutoQualityAdapter(
            onLog: msg => _logService.Info("AutoQuality", msg),
            initialPreset: "Medium");

        // Apply initial bitrate hint
        ApplyBitrateForPreset("Medium");

        _ = Task.Run(() => AutoQualityLoopAsync(_lifetimeCts.Token));
    }

    private async Task AutoQualityLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1500, ct);

                // Stop auto-quality when user selected a fixed preset (not "Auto").
                if (!string.Equals(QualityPreset?.Trim(), "Auto", StringComparison.OrdinalIgnoreCase))
                {
                    _logService.Info("AutoQuality", "stopped_preset_not_auto_" + QualityPreset);
                    Interlocked.Exchange(ref _autoQualityLoopStarted, 0);
                    _autoQualityAdapter = null;
                    return;
                }

                if (_realPeerAgent is null || _autoQualityAdapter is null) continue;

                using var stats = await _realPeerAgent.PeerConnection.GetSimpleStatsAsync();
                var vsList = stats.GetStats<Microsoft.MixedReality.WebRTC.PeerConnection.VideoSenderStats>();
                var first = vsList.FirstOrDefault();
                if (first.RtpStatsTimestampUs == 0) continue;

                var s = first;
                var newPreset = _autoQualityAdapter.Update(
                    s.RtpStatsTimestampUs,
                    (long)s.BytesSent,
                    (long)s.FramesSent,
                    (long)s.FramesEncoded);

                if (newPreset is not null)
                {
                    _logService.Info("AutoQuality", $"switching_to_{newPreset}");
                    ApplyBitrateForPreset(newPreset);

                    // Reconfigure capture with new resolution/fps
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var presetCopy = newPreset;
                    _uiContext.Post(async _ =>
                    {
                        try
                        {
                            await ConfigureLocalVideoForAutoPreset(presetCopy);
                        }
                        catch (Exception ex)
                        {
                            _logService.Warn("AutoQuality", "reconfigure_failed_" + ex.Message);
                        }
                        finally
                        {
                            tcs.TrySetResult();
                        }
                    }, null);
                    await tcs.Task;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logService.Warn("AutoQuality", "loop_error_" + ex.Message);
            }
        }
    }

    private void ApplyBitrateForPreset(string preset)
    {
        var profile = QualityProfiles.Resolve(preset);
        try
        {
            _realPeerAgent?.PeerConnection.SetBitrate(
                minBitrateBps: (uint)(profile.BitrateKbps * 500),  // ~50% of target as floor
                startBitrateBps: (uint)(profile.BitrateKbps * 1000),
                maxBitrateBps: (uint)(profile.BitrateKbps * 1200)); // ~120% ceiling
        }
        catch (Exception ex)
        {
            _logService.Warn("AutoQuality", "set_bitrate_failed_" + ex.Message);
        }
    }

    private async Task ConfigureLocalVideoForAutoPreset(string preset)
    {
        if (_role != ConnectionRole.Host || _realPeerAgent is null) return;

        // Use preset directly without changing the user-visible QualityPreset property (avoids UI flicker).
        // ConfigureLocalVideoAsync теперь сам отправляет screen_meta.
        await ConfigureLocalVideoAsync(_realPeerAgent, presetOverride: preset);
    }

    private async Task ConfigureLocalVideoAsync(IPeerConnectionAgent peerAgent, string? presetOverride = null)
    {
        var profile  = QualityProfiles.Resolve(presetOverride ?? QualityPreset);
        var displays = DisplayEnumerator.GetDisplays();
        if (displays.Count == 0)
        {
            _logService.Warn("ScreenCapture", "no_display_detected_for_local_video");
            return;
        }

        int captureX, captureY, width, height;
        string displayIdForLog;
        var forceGdi = false;
        var selectedDisplay = DisplayId?.Trim() ?? "";

        if (string.Equals(selectedDisplay, "All", StringComparison.OrdinalIgnoreCase))
        {
            // Capture the entire virtual desktop spanning all monitors.
            // DXGI Output Duplication can only capture one output, so force GDI.
            var minX = displays.Min(d => d.X);
            var minY = displays.Min(d => d.Y);
            var maxR = displays.Max(d => d.X + d.Width);
            var maxB = displays.Max(d => d.Y + d.Height);
            captureX       = minX;
            captureY       = minY;
            width          = Math.Max(1, maxR - minX);
            height         = Math.Max(1, maxB - minY);
            displayIdForLog = "ALL";
            forceGdi = true;
        }
        else
        {
            // Specific display by ID, fallback to primary
            DisplaySource? selected = null;
            if (!string.IsNullOrWhiteSpace(selectedDisplay))
            {
                selected = displays.FirstOrDefault(d => string.Equals(d.Id, selectedDisplay, StringComparison.OrdinalIgnoreCase));
            }
            selected ??= displays.FirstOrDefault(d => d.IsPrimary) ?? displays.First();

            captureX       = selected.X;
            captureY       = selected.Y;
            width          = Math.Max(1, selected.Width);
            height         = Math.Max(1, selected.Height);
            displayIdForLog = selected.Id;
        }

        // Scale down to profile resolution while keeping aspect ratio.
        var outWidth = width;
        var outHeight = height;
        if (profile.Width > 0 && profile.Height > 0 && (width > profile.Width || height > profile.Height))
        {
            var scale = Math.Min((double)profile.Width / width, (double)profile.Height / height);
            outWidth = Math.Max(2, (int)(width * scale) & ~1);   // even number for codec
            outHeight = Math.Max(2, (int)(height * scale) & ~1);
        }

        await peerAgent.ConfigureLocalVideoAsync(new LocalVideoOptions
        {
            CaptureX     = captureX,
            CaptureY     = captureY,
            SourceWidth  = width,
            SourceHeight = height,
            Width        = outWidth,
            Height       = outHeight,
            Fps          = profile.Fps,
            ForceGdi     = forceGdi
        });
        _logService.Info("ScreenCapture", $"local_video_configured_{displayIdForLog}_{width}x{height}->({outWidth}x{outHeight})_{profile.Fps}fps_{profile.Name}");

        _localScreenMeta = new ScreenMetaPayload
        {
            CaptureX  = captureX,
            CaptureY  = captureY,
            Width     = width,
            Height    = height,
            DisplayId = displayIdForLog
        };

        // Отправляем сразу после конфигурирования: покрывает initial startup,
        // auto-preset и display switch. Если DC ещё не открыт — setup-loop
        // (SendInitDataChannelMessagesWithRetryAsync) дослылает позже.
        if (_dataChannelCoordinator is not null)
        {
            try
            {
                await _dataChannelCoordinator.SendScreenMetaAsync(_localScreenMeta, _lifetimeCts.Token);
            }
            catch (Exception ex)
            {
                _logService.Debug("ScreenCapture", "screen_meta_send_deferred_" + ex.Message);
            }
        }
    }

    private void OnRemoteVideoFrameReceived(RemoteVideoFrame frame)
    {
        TouchP2PActivity(); // viewer: receiving frames = P2P alive
        UpdateVideoStats(frame);

        // Capture buffer ref before posting — the frame.Buffer will be returned to pool after rendering
        var frameBuffer = frame.Buffer;

        _uiContext.Post(_ =>
        {
            if (_remoteFrameBitmap is null)
            {
                _logService.Info("RemoteScreen", "first_remote_video_frame_received");
                _firstRemoteFrameTcs?.TrySetResult(true);
            }

            if (_remoteFrameBitmap is null
                || _remoteFrameBitmap.PixelWidth  != frame.Width
                || _remoteFrameBitmap.PixelHeight != frame.Height)
            {
                _remoteFrameBitmap = new WriteableBitmap(
                    frame.Width, frame.Height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null);
                RemoteFrameImage = _remoteFrameBitmap;
                OnPropertyChanged(nameof(RemoteFrameImage));
            }

            // Lock/Unlock + direct memcpy is faster than WritePixels (skips validation overhead)
            _remoteFrameBitmap.Lock();
            try
            {
                var backBuffer = _remoteFrameBitmap.BackBuffer;
                var bmpStride = _remoteFrameBitmap.BackBufferStride;
                var srcStride = frame.Stride;
                var h = frame.Height;

                if (bmpStride == srcStride)
                {
                    unsafe
                    {
                        fixed (byte* pSrc = frameBuffer)
                        {
                            Buffer.MemoryCopy(pSrc, (void*)backBuffer, bmpStride * h, srcStride * h);
                        }
                    }
                }
                else
                {
                    var rowBytes = Math.Min(bmpStride, srcStride);
                    unsafe
                    {
                        fixed (byte* pSrc = frameBuffer)
                        {
                            for (var y = 0; y < h; y++)
                            {
                                Buffer.MemoryCopy(
                                    pSrc + y * srcStride,
                                    (byte*)backBuffer + y * bmpStride,
                                    bmpStride, rowBytes);
                            }
                        }
                    }
                }

                _remoteFrameBitmap.AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
            }
            finally
            {
                _remoteFrameBitmap.Unlock();
            }

            // Return buffer to pool for reuse
            _realPeerAgent?.ReturnViewerBuffer(frameBuffer);
        }, null);
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
            DisplayId     = ViewerRequestedDisplayId ?? string.Empty,
            QuickReconnect = quickReconnect
        };

        try
        {
            await _dataChannelCoordinator.SendHostVideoSettingsRequestAsync(payload, _lifetimeCts.Token);
            StatusText = quickReconnect
                ? "Запрошен быстрый реконнект видео"
                : "Запрошено применение настроек видео";
            OnPropertyChanged(nameof(StatusText));
            _logService.Info("RemoteScreen",
                $"viewer_settings_request_sent_{payload.QualityPreset}_{payload.DisplayId}_reconnect_{payload.QuickReconnect}");
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

        try { await _hostVideoSettingsLock.WaitAsync(_lifetimeCts.Token); }
        catch (OperationCanceledException) { return; }

        var oldQuality = QualityPreset;
        var oldDisplay = DisplayId;

        var newQuality = string.IsNullOrWhiteSpace(payload.QualityPreset) ? oldQuality : payload.QualityPreset;
        var newDisplay = string.IsNullOrWhiteSpace(payload.DisplayId)     ? oldDisplay : payload.DisplayId;

        // Must set WPF-bound properties AND SaveSettings on the UI thread:
        // SaveSettings → RefreshAvailableDisplayIds → AvailableDisplayIds.Clear/Add (ObservableCollection).
        await _uiContextInvokeAsync(() =>
        {
            QualityPreset = newQuality;
            DisplayId     = newDisplay;
            SaveSettings();
            return true;
        });

        try
        {
            // Apply encoder bitrate for the new quality preset.
            ApplyBitrateForPreset(QualityPreset);

            // ConfigureLocalVideoAsync теперь сам отправляет screen_meta.
            await ConfigureLocalVideoAsync(_realPeerAgent);

            if (payload.QuickReconnect && _signalingCoordinator is not null && _signalingClient is not null)
            {
                await Task.Delay(80, _lifetimeCts.Token);
                await _signalingCoordinator.StartAsCallerAsync(_currentSessionId, _lifetimeCts.Token);
                _logService.Info("Signaling", "host_quick_reconnect_offer_sent");
            }

            _logService.Info("RemoteScreen",
                $"host_settings_applied_{QualityPreset}_{DisplayId}_reconnect_{payload.QuickReconnect}");
        }
        catch (Exception ex)
        {
            // Rollback on UI thread and WAIT for completion before releasing lock.
            await _uiContextInvokeAsync(() =>
            {
                QualityPreset = oldQuality;
                DisplayId     = oldDisplay;
                SaveSettings();
                return true;
            });
            _logService.Warn("RemoteScreen", "host_settings_apply_failed_" + ex.Message);
        }
        finally
        {
            _hostVideoSettingsLock.Release();
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
        ids.Add("All");

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
                ViewerRequestedDisplayId = ids[0]; // setter already fires OnPropertyChanged
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
                Id        = d.Id,
                Name      = d.Name ?? string.Empty,
                IsPrimary = d.IsPrimary
            })
            .ToList();
        if (displays.Count == 0)
        {
            displays.Add(new DisplayInfoPayload { Id = "DISPLAY1", Name = "DISPLAY1", IsPrimary = true });
        }

        await dc.SendHostDisplaysAsync(new HostDisplaysPayload { Displays = displays }, ct);
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
        ids.Add("All");

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
            ViewerAvailableDisplayIds.Add("All");
        }

        if (string.IsNullOrWhiteSpace(ViewerRequestedDisplayId))
        {
            ViewerRequestedDisplayId = ViewerAvailableDisplayIds[0]; // setter already fires OnPropertyChanged
        }
    }

}
