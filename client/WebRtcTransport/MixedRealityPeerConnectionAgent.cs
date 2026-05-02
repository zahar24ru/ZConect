using Microsoft.MixedReality.WebRTC;
using ScreenCapture;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WebRtcTransport;

public sealed class MixedRealityPeerConnectionAgent : IPeerConnectionAgent, IDisposable
{
    private readonly PeerConnection _peer = new();
    private readonly TransportSettings _settings;
    /// <summary>Expose settings for stale-preload detection (e.g. TURN credentials changed after preload).</summary>
    public TransportSettings? Settings => _settings;
    private readonly Action<string>? _onLog;
    private readonly object _sync = new();
    private TaskCompletionSource<string>? _pendingSdp;
    private ExternalVideoTrackSource? _videoSource;
    private LocalVideoTrack? _localVideoTrack;
    private Transceiver? _videoTransceiver;
    private readonly object _captureSync = new();
    private LocalVideoOptions? _videoOptions;
    private bool _localTrackAttached;
    private bool _initialized;
    private bool _disposed;
    private int _remoteFrames;

    // ── High-precision capture thread ──
    private Thread? _captureThread;
    private volatile bool _captureRunning;
    private volatile int _captureIntervalUs; // microseconds between frames

    // ── Double-buffered capture ──
    // Two pinned buffers: capture thread writes to _backBuffer, encoder reads from _frontBuffer.
    // Swap after each successful capture.
    private byte[]? _frontBuffer;
    private byte[]? _backBuffer;
    private GCHandle _frontHandle;
    private GCHandle _backHandle;
    private int _activeBuffer; // 0 = _frontBuffer is current, 1 = _backBuffer is current

    // ── Reusable DXGI scaling buffer + pinned wrapper Bitmap ──
    private byte[]? _dxgiScratchBuffer;
    private GCHandle _scratchPinHandle;
    private Bitmap? _scratchBitmap;
    private int _scratchBmpW, _scratchBmpH;

    // ── DXGI capture (primary) ──
    private DxgiScreenCapture? _dxgi;
    private bool _useDxgi;

    // ── DXGI multi-output stitching (for "All" monitors) ──
    private DxgiMultiOutputCapture? _dxgiMulti;
    private bool _useDxgiMulti;

    // ── GDI capture (fallback) ──
    private Bitmap? _captureBitmap;
    private Graphics? _captureGraphics;
    private Bitmap? _sourceBitmap;
    private Graphics? _sourceGraphics;

    // ── Frame skip & adaptive FPS ──
    private byte[]? _prevCaptureBuffer;
    private int _unchangedStreak;
    private int _baseFps = 30;
    private int _currentFps = 30;
    private int _skippedFrames;
    private int _totalCapturedFrames;
    private const int SamplePixelStride = 197;

    // ── Viewer-side frame buffer pool ──
    private readonly ConcurrentBag<byte[]> _viewerBufferPool = new();

    public event Action<string>? LocalIceCandidateGenerated;
    public event Action<DataChannel>? DataChannelAdded;
    public event Action<RemoteVideoFrame>? RemoteVideoFrameReceived;
    public event Action? IceStateFailed;
    public event Action? IceStateDisconnected;
    public event Action? IceStateReconnected;
    /// <summary>Tracks whether we've seen Disconnected since last Connected — used to distinguish
    /// initial Connected transition (fired once, not a "reconnect") from recovery after Disconnected.</summary>
    private bool _iceWasDisconnected;
    /// <summary>Fired when DXGI reports ACCESS_LOST (desktop switch: UAC, lock screen, Ctrl+Alt+Del).</summary>
    public event Action? DesktopAccessLost;

    /// <summary>Fired after capture reinit on a new desktop (for cursor/input resync).</summary>
    public event Action<string>? DesktopReinited;

    /// <summary>
    /// Callback to switch capture thread to the current active desktop.
    /// Called on the capture thread when DXGI reports AccessLost.
    /// Must call OpenInputDesktop → SetThreadDesktop. Returns true if switched.
    /// </summary>
    public Func<bool>? TrySwitchToActiveDesktop { get; set; }

    /// <summary>Set to true when DXGI reports AccessLost. Reset when DXGI is reinitialized.</summary>
    public bool IsDesktopAccessLost => _desktopAccessLost;
    private volatile bool _desktopAccessLost;

    /// <summary>Force DXGI reinit on next capture loop iteration.
    /// Called by DesktopMonitor on ANY desktop switch (both Default→Winlogon AND Winlogon→Default).</summary>
    public void ForceDesktopAccessLost()
    {
        _desktopAccessLost = true;
    }

    /// <summary>Desktop name when DXGI was last initialized (e.g. "Default" or "Winlogon").</summary>
    private string? _dxgiInitDesktopName;

    /// <summary>
    /// Get current active desktop name. Can be called from any thread.
    /// Uses OpenInputDesktop + GetUserObjectInformation (same as DesktopMonitor).
    /// </summary>
    public static Func<string?>? GetCurrentDesktopName { get; set; }

    /// <summary>True when GDI fallback is active on Winlogon (DXGI failed, thread was switched to Winlogon desktop).</summary>
    private bool IsWinlogonGdiFallback => !_useDxgi && !_useDxgiMulti && _dxgiInitDesktopName is not null && !_dxgiInitDesktopName.Equals("Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns (skippedFrames, totalFrames) since last call, then resets counters.</summary>
    public (int Skipped, int Total) ConsumeFrameSkipStats()
    {
        lock (_captureSync)
        {
            var s = _skippedFrames;
            var t = _totalCapturedFrames;
            _skippedFrames = 0;
            _totalCapturedFrames = 0;
            return (s, t);
        }
    }

    /// <summary>Expose PeerConnection for stats/bitrate control from ViewModel.</summary>
    public PeerConnection PeerConnection => _peer;

    public MixedRealityPeerConnectionAgent(TransportSettings settings, Action<string>? onLog = null)
    {
        _settings = settings;
        _onLog = onLog;
    }

    public async Task InitializeAsync(CancellationToken ct = default, bool includeVideoTransceiver = true)
    {
        if (_initialized) return;

        var cfg = BuildConfiguration(_settings);
        _onLog?.Invoke($"ICE_CONFIG: servers={cfg.IceServers.Count} transport={cfg.IceTransportType}");
        foreach (var srv in cfg.IceServers)
        {
            var hasCreds = !string.IsNullOrEmpty(srv.TurnUserName);
            _onLog?.Invoke($"ICE_SERVER: {string.Join(",", srv.Urls)}{(hasCreds ? $" user={srv.TurnUserName}" : "")}");
        }
        // Warn если TURN URL конфигурирован но password отсутствует (skip в BuildConfiguration) —
        // диагностика для случаев когда relay не будет работать.
        if (!string.IsNullOrWhiteSpace(_settings.TurnUrl) && string.IsNullOrWhiteSpace(_settings.TurnPassword))
            _onLog?.Invoke("TURN_SKIPPED_NO_PASSWORD — STUN-only mode, relay unavailable (configure TurnPassword in service-config.json или через UI Settings)");
        _peer.LocalSdpReadytoSend += OnLocalSdpReady;
        _peer.IceCandidateReadytoSend += OnIceCandidateReady;
        _peer.DataChannelAdded += OnDataChannelAdded;
        _peer.TransceiverAdded += OnTransceiverAdded;
        _peer.VideoTrackAdded += OnVideoTrackAdded;
        _peer.VideoTrackRemoved += OnVideoTrackRemoved;

        await _peer.InitializeAsync(cfg, ct);

        _peer.IceStateChanged += state =>
        {
            _onLog?.Invoke($"ICE_STATE_CHANGED: {state}");
            switch (state)
            {
                case Microsoft.MixedReality.WebRTC.IceConnectionState.Failed:
                    IceStateFailed?.Invoke();
                    break;
                case Microsoft.MixedReality.WebRTC.IceConnectionState.Disconnected:
                    _iceWasDisconnected = true;
                    IceStateDisconnected?.Invoke();
                    break;
                case Microsoft.MixedReality.WebRTC.IceConnectionState.Connected:
                case Microsoft.MixedReality.WebRTC.IceConnectionState.Completed:
                    if (_iceWasDisconnected)
                    {
                        _iceWasDisconnected = false;
                        IceStateReconnected?.Invoke();
                    }
                    break;
            }
        };
        _peer.IceGatheringStateChanged += state =>
        {
            _onLog?.Invoke($"ICE_GATHERING_STATE: {state}");
        };
        _peer.Connected += () =>
        {
            _onLog?.Invoke("PEER_CONNECTED");
        };

        var dcControl = await _peer.AddDataChannelAsync("dc-control", true, true, ct);
        DataChannelAdded?.Invoke(dcControl);
        var dcInput = await _peer.AddDataChannelAsync("dc-input", true, true, ct);
        DataChannelAdded?.Invoke(dcInput);
        var dcClipboard = await _peer.AddDataChannelAsync("dc-clipboard", true, true, ct);
        DataChannelAdded?.Invoke(dcClipboard);
        var dcFile = await _peer.AddDataChannelAsync("dc-file", true, true, ct);
        DataChannelAdded?.Invoke(dcFile);

        if (includeVideoTransceiver)
            AddVideoTransceiverInternal();

        _initialized = true;
    }

    public void AddVideoTransceiverIfNeeded()
    {
        EnsureInitialized();
        if (_videoTransceiver is not null) return;
        AddVideoTransceiverInternal();
    }

    private void AddVideoTransceiverInternal()
    {
        if (_videoTransceiver is not null) return;
        _videoTransceiver = _peer.AddTransceiver(MediaKind.Video);
        try
        {
            _videoTransceiver.DesiredDirection = Transceiver.Direction.ReceiveOnly;
            _videoTransceiver.DirectionChanged += t =>
            {
                _onLog?.Invoke($"video_direction_changed_desired_{t.DesiredDirection}_negotiated_{t.NegotiatedDirection}");
            };
        }
        catch (Exception ex)
        {
            _onLog?.Invoke("video_transceiver_direction_setup_failed_" + ex.Message);
        }
    }

    public Task ConfigureLocalVideoAsync(LocalVideoOptions? options, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (options is null || options.Width <= 0 || options.Height <= 0 || options.Fps <= 0)
            return Task.CompletedTask;

        // Stop capture BEFORE taking lock to avoid deadlock:
        // CaptureLoopThread also takes _captureSync, so Join inside lock would deadlock.
        StopCaptureLoop();

        lock (_captureSync)
        {
            EnsureCaptureResources(options);
            _videoOptions = options;

            if (_videoSource is null)
                _videoSource = ExternalVideoTrackSource.CreateFromArgb32Callback(OnArgb32FrameRequested);

            if (_localVideoTrack is null)
            {
                _localVideoTrack = LocalVideoTrack.CreateFromSource(_videoSource, new LocalVideoTrackInitConfig
                {
                    trackName = "screen-video"
                });
            }

            if (!_localTrackAttached)
            {
                _videoTransceiver = SelectBestVideoTransceiverForSending();
                _videoTransceiver ??= _peer.AddTransceiver(MediaKind.Video);
                _videoTransceiver.LocalVideoTrack = _localVideoTrack;
                _localTrackAttached = true;
                _onLog?.Invoke("local_video_track_attached_once");
            }

            try
            {
                if (_videoTransceiver is not null)
                {
                    _videoTransceiver.DesiredDirection = Transceiver.Direction.SendOnly;
                    _onLog?.Invoke($"local_video_attached_desired_{_videoTransceiver.DesiredDirection}");
                }
            }
            catch (Exception ex)
            {
                _onLog?.Invoke("video_transceiver_set_sendonly_failed_" + ex.Message);
            }

            StartCaptureLoop(options.Fps);
        }

        return Task.CompletedTask;
    }

    public async Task<string> CreateOfferAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        var tcs = CreatePendingSdpSource();
        if (!_peer.CreateOffer())
            throw new InvalidOperationException("CreateOffer returned false.");
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
            return await tcs.Task;
    }

    public async Task<string> CreateAnswerAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        var tcs = CreatePendingSdpSource();
        if (!_peer.CreateAnswer())
            throw new InvalidOperationException("CreateAnswer returned false.");
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
            return await tcs.Task;
    }

    public async Task SetRemoteOfferAsync(string sdp, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _peer.SetRemoteDescriptionAsync(new SdpMessage { Type = SdpMessageType.Offer, Content = sdp });
    }

    public async Task SetRemoteAnswerAsync(string sdp, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _peer.SetRemoteDescriptionAsync(new SdpMessage { Type = SdpMessageType.Answer, Content = sdp });
    }

    public Task AddIceCandidateAsync(string candidate, CancellationToken ct = default)
    {
        EnsureInitialized();
        _peer.AddIceCandidate(new IceCandidate { SdpMid = "0", SdpMlineIndex = 0, Content = candidate });
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Capture pipeline: DXGI primary → GDI fallback, double-buffered
    // ═══════════════════════════════════════════════════════════════════

    private void EnsureCaptureResources(LocalVideoOptions options)
    {
        var outW = options.Width;
        var outH = options.Height;
        var bufSize = outW * outH * 4;
        var needRealloc = _frontBuffer is null || _frontBuffer.Length != bufSize;

        var regionChanged = _videoOptions is null
                            || _videoOptions.CaptureX != options.CaptureX
                            || _videoOptions.CaptureY != options.CaptureY
                            || _videoOptions.SourceWidth != options.SourceWidth
                            || _videoOptions.SourceHeight != options.SourceHeight
                            || _videoOptions.ForceGdi != options.ForceGdi;

        if (!needRealloc && !regionChanged && _useDxgiMulti && _dxgiMulti is not null)
            return;
        if (!needRealloc && !regionChanged && _useDxgi && _dxgi is not null)
            return;
        if (!needRealloc && !regionChanged && !_useDxgi && !_useDxgiMulti && _captureBitmap is not null)
            return;

        ReleaseCaptureResources();

        // Allocate double-buffered pinned arrays
        EnsurePinnedBuffers(bufSize);

        // Try DXGI capture
        _useDxgi = false;
        _useDxgiMulti = false;

        if (options.ForceGdi)
        {
            // Multi-monitor "All" — try DXGI stitching first, fall back to GDI
            _onLog?.Invoke("capture_dxgi_multi_initializing...");
            try
            {
                var multi = new DxgiMultiOutputCapture();
                // Run Initialize with timeout to avoid hanging on problematic adapters (VPN virtual adapters, etc.)
                var initTask = Task.Run(() => multi.Initialize());
                if (initTask.Wait(5000))
                {
                    _dxgiMulti = multi;
                    _useDxgiMulti = true;
                    _onLog?.Invoke($"capture_dxgi_multi_ok_{_dxgiMulti.Width}x{_dxgiMulti.Height}");
                }
                else
                {
                    _onLog?.Invoke("capture_dxgi_multi_timeout_5s_falling_back_gdi");
                    try { multi.Dispose(); } catch { }
                }
            }
            catch (Exception ex)
            {
                _onLog?.Invoke("capture_dxgi_multi_failed_falling_back_gdi_" + ex.Message);
                _dxgiMulti?.Dispose();
                _dxgiMulti = null;
            }
        }
        else
        {
            // Single monitor — try DXGI single output
            try
            {
                _dxgi = new DxgiScreenCapture();
                _dxgi.InitializeForRegion(options.CaptureX, options.CaptureY);
                _useDxgi = true;
                _onLog?.Invoke($"capture_dxgi_ok_{_dxgi.Width}x{_dxgi.Height}");

                // Pre-allocate scratch buffer for DXGI scaling path
                if (options.NeedsScaling || _dxgi.Width != outW || _dxgi.Height != outH)
                {
                    _dxgiScratchBuffer = new byte[_dxgi.Width * _dxgi.Height * 4];
                    _onLog?.Invoke($"capture_dxgi_scaling_{_dxgi.Width}x{_dxgi.Height}->{outW}x{outH}");
                }
            }
            catch (Exception ex)
            {
                _onLog?.Invoke("capture_dxgi_failed_falling_back_gdi_" + ex.Message);
                _dxgi?.Dispose();
                _dxgi = null;
            }
        }

        if (!_useDxgi && !_useDxgiMulti)
        {
            _captureBitmap = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
            _captureGraphics = Graphics.FromImage(_captureBitmap);

            if (options.NeedsScaling)
            {
                _sourceBitmap = new Bitmap(options.SourceWidth, options.SourceHeight, PixelFormat.Format32bppArgb);
                _sourceGraphics = Graphics.FromImage(_sourceBitmap);
                _captureGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                _captureGraphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                _captureGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
            }
            _onLog?.Invoke($"capture_gdi_{outW}x{outH}");
        }

        _prevCaptureBuffer = null;
    }

    /// <summary>Get the current back buffer (capture target).</summary>
    private byte[] BackBuf => _activeBuffer == 0 ? _backBuffer! : _frontBuffer!;

    /// <summary>Swap front/back buffers and return the new front handle for the encoder.</summary>
    private void SwapBuffers()
    {
        _activeBuffer ^= 1;
    }

    /// <summary>Get GCHandle of the current front buffer (for encoder).</summary>
    private GCHandle FrontHandle => _activeBuffer == 0 ? _frontHandle : _backHandle;

    private void StartCaptureLoop(int fps)
    {
        StopCaptureLoop();

        _baseFps = fps;
        _currentFps = fps;
        _unchangedStreak = 0;
        _prevCaptureBuffer = null;
        _captureIntervalUs = Math.Max(1000, 1_000_000 / fps);
        _captureRunning = true;

        _captureThread = new Thread(CaptureLoopThread)
        {
            IsBackground = true,
            Name = "CaptureLoop",
            Priority = ThreadPriority.AboveNormal
        };
        _captureThread.Start();
    }

    private void StopCaptureLoop()
    {
        _captureRunning = false;
        _captureThread?.Join(500);
        _captureThread = null;
    }

    private void CaptureLoopThread()
    {
        // Switch new thread to the current active desktop (TigerVNC pattern).
        TrySwitchToActiveDesktop?.Invoke();
        _dxgiInitDesktopName = GetCurrentDesktopName?.Invoke() ?? "Default";
        _onLog?.Invoke($"capture_thread_started_on_{_dxgiInitDesktopName}");

        var sw = new Stopwatch();
        sw.Start();
        var consecutiveErrors = 0;

        while (_captureRunning)
        {
            var frameStart = sw.ElapsedTicks;

            bool notReady;
            lock (_captureSync)
            {
                notReady = _videoOptions is null || _frontBuffer is null || _backBuffer is null;
            }
            if (notReady)
            {
                Thread.Sleep(10);
                continue;
            }

            // TigerVNC pattern: EVERY FRAME check if desktop changed.
            if (!InputDesktopSelected())
            {
                ReinitOnNewDesktop();
                Thread.Sleep(50);
                continue;
            }

            lock (_captureSync)
            {
                if (_videoOptions is null || _frontBuffer is null || _backBuffer is null)
                    continue;

                Interlocked.Increment(ref _totalCapturedFrames);

                bool changed;
                try
                {
                    if (_useDxgiMulti && _dxgiMulti is not null)
                        changed = CaptureDxgiMulti();
                    else if (_useDxgi && _dxgi is not null)
                        changed = CaptureDxgi();
                    else
                        changed = CaptureGdi();

                    consecutiveErrors = 0;
                }
                catch (Exception ex)
                {
                    changed = false;
                    consecutiveErrors++;
                    if (consecutiveErrors <= 3 || consecutiveErrors % 100 == 0)
                        _onLog?.Invoke($"capture_exception_{consecutiveErrors}: {ex.Message}");
                    Thread.Sleep(50);
                }

                if (changed)
                {
                    SwapBuffers();
                    _unchangedStreak = 0;
                    AdjustCaptureSpeed(active: true);
                }
                else
                {
                    Interlocked.Increment(ref _skippedFrames);
                    _unchangedStreak++;
                    AdjustCaptureSpeed(active: false);
                }
            }

            var targetTicks = frameStart + _captureIntervalUs * (Stopwatch.Frequency / 1_000_000);
            while (sw.ElapsedTicks < targetTicks)
            {
                var remainingMs = (targetTicks - sw.ElapsedTicks) * 1000 / Stopwatch.Frequency;
                if (remainingMs > 2)
                    Thread.Sleep(1);
                else
                    Thread.SpinWait(100);
            }
        }
    }

    // ── Desktop switch (TigerVNC pattern) ──────────────────────────

    private bool InputDesktopSelected()
    {
        var current = GetCurrentDesktopName?.Invoke();
        if (current is null) return true;
        _dxgiInitDesktopName ??= current;
        return current.Equals(_dxgiInitDesktopName, StringComparison.OrdinalIgnoreCase);
    }

    private void ReinitOnNewDesktop()
    {
        var opts = _videoOptions;
        if (opts is null) return;
        var newDesktop = GetCurrentDesktopName?.Invoke() ?? "Default";
        _onLog?.Invoke($"desktop_changed_{_dxgiInitDesktopName}_to_{newDesktop}");

        // 1. Release ALL
        ReleaseCaptureResources();
        // 1a. Fix DXGI cascading failure (known bug 2026-04-18): после первого
        // winlogon_dxgi_failed_gdi DXGI на Default тоже ломался с E_ACCESSDENIED
        // because stale D3D11 references держались в finalizer queue до
        // gc-trigger'а. Force collect + wait for finalizers гарантирует, что
        // unmanaged DX resources полностью released перед reinit.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        // 2. Switch thread
        TrySwitchToActiveDesktop?.Invoke();
        // 3. Wait
        Thread.Sleep(300);
        // 4. Reinit
        var isWinlogon = !newDesktop.Equals("Default", StringComparison.OrdinalIgnoreCase);
        if (isWinlogon)
        {
            try
            {
                _dxgi = new DxgiScreenCapture();
                _dxgi.Initialize(0, 0);
                _useDxgi = true; _useDxgiMulti = false;
                _onLog?.Invoke($"winlogon_dxgi_{_dxgi.Width}x{_dxgi.Height}");
                // allocate buffers
                var bufSize = opts.Width * opts.Height * 4;
                EnsurePinnedBuffers(bufSize);
                if (opts.NeedsScaling || _dxgi.Width != opts.Width || _dxgi.Height != opts.Height)
                    _dxgiScratchBuffer = new byte[_dxgi.Width * _dxgi.Height * 4];
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"winlogon_dxgi_failed_gdi: {ex.Message}");
                _useDxgi = false; _useDxgiMulti = false;
                var bufSize = opts.Width * opts.Height * 4;
                EnsurePinnedBuffers(bufSize);
                _captureBitmap = new Bitmap(opts.Width, opts.Height, PixelFormat.Format32bppArgb);
                _captureGraphics = Graphics.FromImage(_captureBitmap);
                _onLog?.Invoke($"winlogon_gdi_{opts.Width}x{opts.Height}");
            }
        }
        else
        {
            EnsureCaptureResources(opts);
        }
        _dxgiInitDesktopName = newDesktop;
        _desktopAccessLost = false;
        _onLog?.Invoke($"reinit_on_{newDesktop}");
        DesktopReinited?.Invoke(newDesktop);
    }

    // ── DXGI capture path ────────────────────────────────────────────

    private bool CaptureDxgi()
    {
        var acquireResult = _dxgi!.TryAcquireFrameEx(0);
        if (acquireResult == AcquireResult.AccessLost)
        {
            if (!_desktopAccessLost) // log only first occurrence
            {
                _desktopAccessLost = true;
                _onLog?.Invoke("dxgi_access_lost");
                DesktopAccessLost?.Invoke();
            }
            return false;
        }
        if (acquireResult != AcquireResult.Success)
            return false;

        try
        {
            var opts = _videoOptions!;
            var outW = opts.Width;
            var outH = opts.Height;
            var dxgiW = _dxgi.Width;
            var dxgiH = _dxgi.Height;
            var buf = BackBuf;

            if (!opts.NeedsScaling && dxgiW == outW && dxgiH == outH)
            {
                var totalPixels = dxgiW * dxgiH;
                var dirtyArea = _dxgi.DirtyRectPixelArea;

                if (_dxgi.DirtyRectCount > 0 && dirtyArea < totalPixels / 2)
                {
                    var front = _activeBuffer == 0 ? _frontBuffer! : _backBuffer!;
                    Buffer.BlockCopy(front, 0, buf, 0, buf.Length);
                    _dxgi.CopyDirtyRectsTo(buf);
                }
                else
                {
                    _dxgi.CopyFrameTo(buf);
                }
            }
            else
            {
                var srcBmp = EnsureScratchBitmap(dxgiW, dxgiH);
                _dxgi.CopyFrameTo(_dxgiScratchBuffer!);

                if (_captureBitmap is null || _captureBitmap.Width != outW || _captureBitmap.Height != outH)
                {
                    _captureGraphics?.Dispose();
                    _captureBitmap?.Dispose();
                    _captureBitmap = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
                    _captureGraphics = Graphics.FromImage(_captureBitmap);
                    _captureGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    _captureGraphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                }
                _captureGraphics!.DrawImage(srcBmp, 0, 0, outW, outH);

                var rect = new Rectangle(0, 0, outW, outH);
                var data = _captureBitmap!.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    Marshal.Copy(data.Scan0, buf, 0, outW * outH * 4);
                }
                finally
                {
                    _captureBitmap.UnlockBits(data);
                }
            }

            return true;
        }
        finally
        {
            _dxgi.ReleaseFrame();
        }
    }

    // ── DXGI multi-output capture path (All monitors) ─────────────────

    private bool CaptureDxgiMulti()
    {
        var acquireResult = _dxgiMulti!.TryAcquireFramesEx(0);
        if (acquireResult == AcquireResult.AccessLost)
        {
            if (!_desktopAccessLost)
            {
                _desktopAccessLost = true;
                _onLog?.Invoke("dxgi_multi_access_lost");
                DesktopAccessLost?.Invoke();
            }
            return false;
        }
        if (acquireResult != AcquireResult.Success)
            return false;

        try
        {
            var opts = _videoOptions!;
            var outW = opts.Width;
            var outH = opts.Height;
            var nativeW = _dxgiMulti.Width;
            var nativeH = _dxgiMulti.Height;
            var buf = BackBuf;

            if (nativeW == outW && nativeH == outH)
            {
                // F-13 (external audit 2026-04-18): CompositeFramesTo копирует
                // ТОЛЬКО outputs где был новый frame (slot.FrameAcquired=true).
                // BackBuf содержит whatever было после предыдущего swap — может
                // быть stale frame TWO назад. Копируем current FrontBuf → BackBuf
                // СНАЧАЛА, затем overlay только изменившиеся outputs. Результат:
                // unchanged monitors show последний известный frame; changed
                // monitors — fresh.
                // Parity с single-output dirty-rect path (line 637-639).
                var front = _activeBuffer == 0 ? _frontBuffer! : _backBuffer!;
                Buffer.BlockCopy(front, 0, buf, 0, buf.Length);
                _dxgiMulti.CompositeFramesTo(buf);
            }
            else
            {
                // Scaling branch — scratch buffer persistent (EnsureScratchBitmap
                // не clears content), поэтому composite overlays previous frame в
                // scratch → DrawImage rescales в BackBuf. Корректное поведение
                // для non-changed regions без дополнительного copy.
                var srcBmp = EnsureScratchBitmap(nativeW, nativeH);
                _dxgiMulti.CompositeFramesTo(_dxgiScratchBuffer!);

                if (_captureBitmap is null || _captureBitmap.Width != outW || _captureBitmap.Height != outH)
                {
                    _captureGraphics?.Dispose();
                    _captureBitmap?.Dispose();
                    _captureBitmap = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
                    _captureGraphics = Graphics.FromImage(_captureBitmap);
                    _captureGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    _captureGraphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                }
                _captureGraphics!.DrawImage(srcBmp, 0, 0, outW, outH);

                var rect = new Rectangle(0, 0, outW, outH);
                var data = _captureBitmap!.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    Marshal.Copy(data.Scan0, buf, 0, outW * outH * 4);
                }
                finally
                {
                    _captureBitmap.UnlockBits(data);
                }
            }

            return true;
        }
        finally
        {
            _dxgiMulti.ReleaseFrames();
        }
    }

    // ── GDI capture path (fallback) ────────────────────────────────────

    private bool CaptureGdi()
    {
        var opts = _videoOptions!;
        if (_captureGraphics is null || _captureBitmap is null)
            return false;

        // On Winlogon fallback: capture from (0,0) — Winlogon has single screen.
        var captureX = IsWinlogonGdiFallback ? 0 : opts.CaptureX;
        var captureY = IsWinlogonGdiFallback ? 0 : opts.CaptureY;

        if (opts.NeedsScaling && _sourceBitmap is not null && _sourceGraphics is not null)
        {
            _sourceGraphics.CopyFromScreen(captureX, captureY, 0, 0,
                new Size(opts.SourceWidth, opts.SourceHeight), CopyPixelOperation.SourceCopy);
            _captureGraphics.DrawImage(_sourceBitmap, 0, 0, opts.Width, opts.Height);
        }
        else
        {
            _captureGraphics.CopyFromScreen(captureX, captureY, 0, 0,
                new Size(opts.Width, opts.Height), CopyPixelOperation.SourceCopy);
        }

        var rect = new Rectangle(0, 0, opts.Width, opts.Height);
        var data = _captureBitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var strideAbs = Math.Abs(data.Stride);
            var bytes = strideAbs * opts.Height;
            var buf = BackBuf;

            var changed = _prevCaptureBuffer is null;
            if (!changed)
            {
                var pixelCount = bytes / 4;
                for (var i = 0; i < pixelCount; i += SamplePixelStride)
                {
                    var offset = i * 4;
                    var current = Marshal.ReadInt32(data.Scan0, offset);
                    var prev = BitConverter.ToInt32(_prevCaptureBuffer!, offset);
                    if (current != prev)
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (changed)
            {
                Marshal.Copy(data.Scan0, buf, 0, bytes);
                if (_prevCaptureBuffer is null || _prevCaptureBuffer.Length != bytes)
                    _prevCaptureBuffer = new byte[bytes];
                Buffer.BlockCopy(buf, 0, _prevCaptureBuffer, 0, bytes);
            }

            return changed;
        }
        finally
        {
            _captureBitmap.UnlockBits(data);
        }
    }

    private void AdjustCaptureSpeed(bool active)
    {
        int targetFps;
        if (active)
            targetFps = _baseFps;
        else if (_unchangedStreak < 5)
            targetFps = _baseFps;
        else if (_unchangedStreak < 30)
            targetFps = Math.Max(5, _baseFps / 2);
        else
            targetFps = Math.Max(2, _baseFps / 4);

        if (targetFps != _currentFps)
        {
            _currentFps = targetFps;
            _captureIntervalUs = Math.Max(1000, 1_000_000 / targetFps);
        }
    }

    private void OnArgb32FrameRequested(in FrameRequest request)
    {
        lock (_captureSync)
        {
            if (_videoOptions is null || !FrontHandle.IsAllocated)
                return;

            var frame = new Argb32VideoFrame
            {
                width = (uint)_videoOptions.Width,
                height = (uint)_videoOptions.Height,
                stride = _videoOptions.Width * 4,
                data = FrontHandle.AddrOfPinnedObject()
            };
            request.Source.CompleteFrameRequest(request.RequestId, request.TimestampMs, in frame);
        }
    }

    private void OnArgb32RemoteVideoFrameReady(Argb32VideoFrame frame)
    {
        var n = Interlocked.Increment(ref _remoteFrames);
        var width = (int)frame.width;
        var height = (int)frame.height;
        var stride = frame.stride;
        if (width <= 0 || height <= 0 || stride <= 0)
            return;

        if (n == 1 || (n % 60) == 0)
            _onLog?.Invoke($"remote_video_frame_{n}_{width}x{height}_stride_{stride}");

        var bytes = stride * height;

        // Reuse buffer from pool if available and correctly sized
        if (!_viewerBufferPool.TryTake(out var buffer) || buffer.Length != bytes)
            buffer = new byte[bytes];

        Marshal.Copy(frame.data, buffer, 0, bytes);
        RemoteVideoFrameReceived?.Invoke(new RemoteVideoFrame
        {
            Buffer = buffer,
            Width = width,
            Height = height,
            Stride = stride
        });
    }

    /// <summary>Return a viewer frame buffer to the pool for reuse.</summary>
    public void ReturnViewerBuffer(byte[] buffer)
    {
        // Keep pool small to avoid unbounded growth
        if (_viewerBufferPool.Count < 4)
            _viewerBufferPool.Add(buffer);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Event handlers & helpers
    // ═══════════════════════════════════════════════════════════════════

    private void OnLocalSdpReady(SdpMessage msg)
    {
        lock (_sync) { _pendingSdp?.TrySetResult(msg.Content); }
    }

    private void OnIceCandidateReady(IceCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Content))
        {
            // Log candidate type for ICE debugging
            var c = candidate.Content;
            var candidateType = "unknown";
            if (c.Contains("typ host")) candidateType = "host";
            else if (c.Contains("typ srflx")) candidateType = "srflx";
            else if (c.Contains("typ relay")) candidateType = "relay";
            else if (c.Contains("typ prflx")) candidateType = "prflx";
            _onLog?.Invoke($"ICE_LOCAL_CANDIDATE: {candidateType} mid={candidate.SdpMid}");

            LocalIceCandidateGenerated?.Invoke(candidate.Content);
        }
    }

    private void OnDataChannelAdded(DataChannel channel) => DataChannelAdded?.Invoke(channel);

    private void OnTransceiverAdded(Transceiver transceiver)
    {
        try
        {
            _onLog?.Invoke($"transceiver_added_kind_{transceiver.MediaKind}_mline_{transceiver.MlineIndex}_desired_{transceiver.DesiredDirection}_neg_{transceiver.NegotiatedDirection}");
        }
        catch { }

        if (transceiver.MediaKind == MediaKind.Video)
        {
            if (_videoTransceiver is null || transceiver.MlineIndex >= 0)
            {
                _videoTransceiver = transceiver;
                _onLog?.Invoke("video_transceiver_selected_from_added");
            }
        }
    }

    private void OnVideoTrackAdded(RemoteVideoTrack track)
    {
        _onLog?.Invoke("remote_video_track_added");
        track.Argb32VideoFrameReady += OnArgb32RemoteVideoFrameReady;
    }

    private void OnVideoTrackRemoved(Transceiver transceiver, RemoteVideoTrack track)
    {
        track.Argb32VideoFrameReady -= OnArgb32RemoteVideoFrameReady;
    }

    private TaskCompletionSource<string> CreatePendingSdpSource()
    {
        lock (_sync)
        {
            _pendingSdp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pendingSdp;
        }
    }

    private static PeerConnectionConfiguration BuildConfiguration(TransportSettings settings)
    {
        var iceServers = new List<IceServer>();
        if (!string.IsNullOrWhiteSpace(settings.StunUrl))
            iceServers.Add(new IceServer { Urls = new List<string> { settings.StunUrl } });

        if (!settings.PreferLanVpnNoTurn)
        {
            // Primary TURN server from flat TurnUrl/TurnUsername/TurnPassword (backward compat
            // с single-server service-config.json / pipe protocol). Full credentials required —
            // mrwebrtc throws generic Exception on InitializeAsync if username или password пустые
            // (observed 2026-04-18 after clean uninstall wiped service-config.TurnPassword).
            var addedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(settings.TurnUrl)
                && !string.IsNullOrWhiteSpace(settings.TurnUsername)
                && !string.IsNullOrWhiteSpace(settings.TurnPassword))
            {
                iceServers.Add(new IceServer
                {
                    Urls = new List<string> { settings.TurnUrl },
                    TurnUserName = settings.TurnUsername,
                    TurnPassword = settings.TurnPassword
                });
                addedUrls.Add(settings.TurnUrl);
            }

            // Additional TURN servers for failover. Duplicates по URL ignored.
            if (settings.TurnServers is not null)
            {
                foreach (var entry in settings.TurnServers)
                {
                    if (entry is null) continue;
                    if (string.IsNullOrWhiteSpace(entry.Url)
                        || string.IsNullOrWhiteSpace(entry.Username)
                        || string.IsNullOrWhiteSpace(entry.Password))
                        continue;
                    if (!addedUrls.Add(entry.Url)) continue;
                    iceServers.Add(new IceServer
                    {
                        Urls = new List<string> { entry.Url },
                        TurnUserName = entry.Username,
                        TurnPassword = entry.Password
                    });
                }
            }
        }

        return new PeerConnectionConfiguration
        {
            IceServers = iceServers,
            IceTransportType = settings.PreferRelay ? IceTransportType.Relay : IceTransportType.All
        };
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Peer connection is not initialized.");
    }

    private Transceiver? SelectBestVideoTransceiverForSending()
    {
        try
        {
            var t = _peer.AssociatedTransceivers.FirstOrDefault(x => x.MediaKind == MediaKind.Video);
            if (t is not null) return t;
        }
        catch (Exception ex)
        {
            _onLog?.Invoke("video_transceiver_select_assoc_failed_" + ex.Message);
        }
        return _videoTransceiver;
    }

    /// <summary>
    /// Returns a pinned Bitmap wrapping _dxgiScratchBuffer. Recreates only when dimensions change.
    /// </summary>
    private Bitmap EnsureScratchBitmap(int w, int h)
    {
        if (_scratchBitmap is not null && _scratchBmpW == w && _scratchBmpH == h)
            return _scratchBitmap;

        _scratchBitmap?.Dispose();
        _scratchBitmap = null;
        if (_scratchPinHandle.IsAllocated) _scratchPinHandle.Free();

        var needed = w * h * 4;
        if (_dxgiScratchBuffer is null || _dxgiScratchBuffer.Length < needed)
            _dxgiScratchBuffer = new byte[needed];

        _scratchPinHandle = GCHandle.Alloc(_dxgiScratchBuffer, GCHandleType.Pinned);
        _scratchBitmap = new Bitmap(w, h, w * 4,
            PixelFormat.Format32bppArgb, _scratchPinHandle.AddrOfPinnedObject());
        _scratchBmpW = w;
        _scratchBmpH = h;
        return _scratchBitmap;
    }

    private void EnsurePinnedBuffers(int bufSize)
    {
        if (_frontBuffer is not null && _frontBuffer.Length >= bufSize) return;
        _frontBuffer = GC.AllocateArray<byte>(bufSize, pinned: true);
        _backBuffer = GC.AllocateArray<byte>(bufSize, pinned: true);
        _frontHandle = GCHandle.Alloc(_frontBuffer, GCHandleType.Pinned);
        _backHandle = GCHandle.Alloc(_backBuffer, GCHandleType.Pinned);
        _activeBuffer = 0;
    }

    private void ReleaseCaptureResources()
    {
        _dxgi?.Dispose();
        _dxgi = null;
        _useDxgi = false;

        _dxgiMulti?.Dispose();
        _dxgiMulti = null;
        _useDxgiMulti = false;

        _scratchBitmap?.Dispose();
        _scratchBitmap = null;
        if (_scratchPinHandle.IsAllocated) _scratchPinHandle.Free();
        _dxgiScratchBuffer = null;
        _scratchBmpW = 0;
        _scratchBmpH = 0;

        _sourceGraphics?.Dispose();
        _sourceGraphics = null;
        _sourceBitmap?.Dispose();
        _sourceBitmap = null;
        _captureGraphics?.Dispose();
        _captureGraphics = null;
        _captureBitmap?.Dispose();
        _captureBitmap = null;

        if (_frontHandle.IsAllocated) _frontHandle.Free();
        if (_backHandle.IsAllocated) _backHandle.Free();
        _frontBuffer = null;
        _backBuffer = null;
        _prevCaptureBuffer = null;
    }

    private void StopCaptureAndReleaseResources()
    {
        StopCaptureLoop();
        lock (_captureSync)
        {
            ReleaseCaptureResources();
            _videoOptions = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopCaptureAndReleaseResources();
        _peer.DataChannelAdded -= OnDataChannelAdded;
        _peer.TransceiverAdded -= OnTransceiverAdded;
        _peer.VideoTrackAdded -= OnVideoTrackAdded;
        _peer.VideoTrackRemoved -= OnVideoTrackRemoved;
        _peer.IceCandidateReadytoSend -= OnIceCandidateReady;
        _peer.LocalSdpReadytoSend -= OnLocalSdpReady;
        _localVideoTrack?.Dispose();
        _videoSource?.Dispose();
        _localTrackAttached = false;
        _peer.Close();
    }
}
