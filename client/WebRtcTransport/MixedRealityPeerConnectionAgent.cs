using Microsoft.MixedReality.WebRTC;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Timers;

namespace WebRtcTransport;

public sealed class MixedRealityPeerConnectionAgent : IPeerConnectionAgent, IDisposable
{
    private readonly PeerConnection _peer = new();
    private readonly TransportSettings _settings;
    private readonly Action<string>? _onLog;
    private readonly object _sync = new();
    private TaskCompletionSource<string>? _pendingSdp;
    private ExternalVideoTrackSource? _videoSource;
    private LocalVideoTrack? _localVideoTrack;
    private Transceiver? _videoTransceiver;
    private Bitmap? _captureBitmap;
    private Graphics? _captureGraphics;
    private byte[]? _captureBuffer;
    private GCHandle _captureHandle;
    private System.Timers.Timer? _captureTimer;
    private readonly object _captureSync = new();
    private LocalVideoOptions? _videoOptions;
    private bool _localTrackAttached;
    private bool _initialized;
    private bool _disposed;
    private int _remoteFrames;

    public event Action<string>? LocalIceCandidateGenerated;
    public event Action<DataChannel>? DataChannelAdded;
    public event Action<RemoteVideoFrame>? RemoteVideoFrameReceived;

    public MixedRealityPeerConnectionAgent(TransportSettings settings, Action<string>? onLog = null)
    {
        _settings = settings;
        _onLog = onLog;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
        {
            return;
        }

        var cfg = BuildConfiguration(_settings);
        _peer.LocalSdpReadytoSend += OnLocalSdpReady;
        _peer.IceCandidateReadytoSend += OnIceCandidateReady;
        _peer.DataChannelAdded += OnDataChannelAdded;
        _peer.TransceiverAdded += OnTransceiverAdded;
        _peer.VideoTrackAdded += OnVideoTrackAdded;
        _peer.VideoTrackRemoved += OnVideoTrackRemoved;

        await _peer.InitializeAsync(cfg, ct);

        // Критично для SCTP: создаем каналы до offer.
        // Важно: локально созданные каналы нужно также "прикрепить" снаружи (UI/координатор),
        // иначе отправка может падать "channel is not ready" до прихода удаленного peer.
        var dcControl = await _peer.AddDataChannelAsync("dc-control", true, true, ct);
        DataChannelAdded?.Invoke(dcControl);
        var dcInput = await _peer.AddDataChannelAsync("dc-input", true, true, ct);
        DataChannelAdded?.Invoke(dcInput);
        var dcClipboard = await _peer.AddDataChannelAsync("dc-clipboard", true, true, ct);
        DataChannelAdded?.Invoke(dcClipboard);
        var dcFile = await _peer.AddDataChannelAsync("dc-file", true, true, ct);
        DataChannelAdded?.Invoke(dcFile);

        // Важно для видео: offer должен содержать video m-line, иначе удаленный peer не сможет прислать video track.
        // Создаем video transceiver всегда (даже если локально не шлем видео на этом peer).
        _videoTransceiver ??= _peer.AddTransceiver(MediaKind.Video);
        try
        {
            // Default to recv-only until a local track is attached (viewer mode).
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

        _initialized = true;
    }

    public Task ConfigureLocalVideoAsync(LocalVideoOptions? options, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (options is null || options.Width <= 0 || options.Height <= 0 || options.Fps <= 0)
        {
            return Task.CompletedTask;
        }

        lock (_captureSync)
        {
            _videoOptions = options;
            if (_videoSource is null)
            {
                _videoSource = ExternalVideoTrackSource.CreateFromArgb32Callback(OnArgb32FrameRequested);
            }

            if (_localVideoTrack is null)
            {
                _localVideoTrack = LocalVideoTrack.CreateFromSource(_videoSource, new LocalVideoTrackInitConfig
                {
                    trackName = "screen-video"
                });
            }

            // Re-attaching LocalVideoTrack repeatedly may crash native mrwebrtc in some versions.
            // Attach once; further reconfigure calls only update capture options/timer.
            if (!_localTrackAttached)
            {
                // Prefer an associated video transceiver (created/associated by applying remote offer)
                // to ensure the answer SDP advertises sending.
                _videoTransceiver = SelectBestVideoTransceiverForSending();
                if (_videoTransceiver is null)
                {
                    _videoTransceiver = _peer.AddTransceiver(MediaKind.Video);
                }

                _videoTransceiver.LocalVideoTrack = _localVideoTrack;
                _localTrackAttached = true;
                _onLog?.Invoke("local_video_track_attached_once");
            }

            try
            {
                // Host mode: only send screen, do not expect remote video from viewer.
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

            EnsureCaptureResources(options);
            StartCaptureTimer(options.Fps);
        }

        return Task.CompletedTask;
    }

    public async Task<string> CreateOfferAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        var tcs = CreatePendingSdpSource();
        if (!_peer.CreateOffer())
        {
            throw new InvalidOperationException("CreateOffer returned false.");
        }
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
        {
            return await tcs.Task;
        }
    }

    public async Task<string> CreateAnswerAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        var tcs = CreatePendingSdpSource();
        if (!_peer.CreateAnswer())
        {
            throw new InvalidOperationException("CreateAnswer returned false.");
        }
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
        {
            return await tcs.Task;
        }
    }

    public async Task SetRemoteOfferAsync(string sdp, CancellationToken ct = default)
    {
        EnsureInitialized();
        var msg = new SdpMessage
        {
            Type = SdpMessageType.Offer,
            Content = sdp
        };
        await _peer.SetRemoteDescriptionAsync(msg);
    }

    public async Task SetRemoteAnswerAsync(string sdp, CancellationToken ct = default)
    {
        EnsureInitialized();
        var msg = new SdpMessage
        {
            Type = SdpMessageType.Answer,
            Content = sdp
        };
        await _peer.SetRemoteDescriptionAsync(msg);
    }

    public Task AddIceCandidateAsync(string candidate, CancellationToken ct = default)
    {
        EnsureInitialized();
        _peer.AddIceCandidate(new IceCandidate
        {
            SdpMid = "0",
            SdpMlineIndex = 0,
            Content = candidate
        });
        return Task.CompletedTask;
    }

    private void OnLocalSdpReady(SdpMessage msg)
    {
        lock (_sync)
        {
            _pendingSdp?.TrySetResult(msg.Content);
        }
    }

    private void OnIceCandidateReady(IceCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Content))
        {
            LocalIceCandidateGenerated?.Invoke(candidate.Content);
        }
    }

    private void OnDataChannelAdded(DataChannel channel)
    {
        DataChannelAdded?.Invoke(channel);
    }

    private void OnTransceiverAdded(Transceiver transceiver)
    {
        try
        {
            _onLog?.Invoke($"transceiver_added_kind_{transceiver.MediaKind}_mline_{transceiver.MlineIndex}_desired_{transceiver.DesiredDirection}_neg_{transceiver.NegotiatedDirection}");
        }
        catch
        {
            // ignore
        }

        if (transceiver.MediaKind == MediaKind.Video)
        {
            // Prefer the first associated transceiver (mline >= 0) as canonical for SDP.
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
        {
            iceServers.Add(new IceServer
            {
                Urls = new List<string> { settings.StunUrl }
            });
        }

        if (!settings.PreferLanVpnNoTurn && !string.IsNullOrWhiteSpace(settings.TurnUrl))
        {
            iceServers.Add(new IceServer
            {
                Urls = new List<string> { settings.TurnUrl },
                TurnUserName = settings.TurnUsername,
                TurnPassword = settings.TurnPassword
            });
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
        {
            throw new InvalidOperationException("Peer connection is not initialized.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
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

    private Transceiver? SelectBestVideoTransceiverForSending()
    {
        try
        {
            // Associated transceivers reflect current SDP m-lines; use them if available.
            var assoc = _peer.AssociatedTransceivers;
            var t = assoc.FirstOrDefault(x => x.MediaKind == MediaKind.Video);
            if (t is not null)
            {
                return t;
            }
        }
        catch (Exception ex)
        {
            _onLog?.Invoke("video_transceiver_select_assoc_failed_" + ex.Message);
        }

        return _videoTransceiver;
    }

    private void EnsureCaptureResources(LocalVideoOptions options)
    {
        var recreate = _captureBitmap is null || _captureBitmap.Width != options.Width || _captureBitmap.Height != options.Height;
        if (!recreate)
        {
            return;
        }

        _captureGraphics?.Dispose();
        _captureBitmap?.Dispose();
        if (_captureHandle.IsAllocated)
        {
            _captureHandle.Free();
        }

        _captureBitmap = new Bitmap(options.Width, options.Height, PixelFormat.Format32bppArgb);
        _captureGraphics = Graphics.FromImage(_captureBitmap);
        _captureBuffer = new byte[options.Width * options.Height * 4];
        _captureHandle = GCHandle.Alloc(_captureBuffer, GCHandleType.Pinned);
    }

    private void StartCaptureTimer(int fps)
    {
        if (_captureTimer is not null)
        {
            _captureTimer.Stop();
            _captureTimer.Dispose();
        }

        var interval = Math.Max(10, 1000 / fps);
        _captureTimer = new System.Timers.Timer(interval);
        _captureTimer.AutoReset = true;
        _captureTimer.Elapsed += OnCaptureTimerElapsed;
        _captureTimer.Start();
    }

    private void OnCaptureTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        lock (_captureSync)
        {
            if (_videoOptions is null || _captureBitmap is null || _captureGraphics is null || _captureBuffer is null)
            {
                return;
            }

            _captureGraphics.CopyFromScreen(_videoOptions.CaptureX, _videoOptions.CaptureY, 0, 0, new Size(_videoOptions.Width, _videoOptions.Height), CopyPixelOperation.SourceCopy);
            var rect = new Rectangle(0, 0, _videoOptions.Width, _videoOptions.Height);
            var data = _captureBitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var strideAbs = Math.Abs(data.Stride);
                var bytes = strideAbs * _videoOptions.Height;
                Marshal.Copy(data.Scan0, _captureBuffer, 0, bytes);
            }
            finally
            {
                _captureBitmap.UnlockBits(data);
            }
        }
    }

    private void OnArgb32FrameRequested(in FrameRequest request)
    {
        lock (_captureSync)
        {
            if (_videoOptions is null || _captureHandle.IsAllocated is false)
            {
                return;
            }

            var frame = new Argb32VideoFrame
            {
                width = (uint)_videoOptions.Width,
                height = (uint)_videoOptions.Height,
                stride = _videoOptions.Width * 4,
                data = _captureHandle.AddrOfPinnedObject()
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
        {
            return;
        }

        if (n == 1 || (n % 60) == 0)
        {
            _onLog?.Invoke($"remote_video_frame_{n}_{width}x{height}_stride_{stride}");
        }

        var bytes = stride * height;
        var buffer = new byte[bytes];
        Marshal.Copy(frame.data, buffer, 0, bytes);
        RemoteVideoFrameReceived?.Invoke(new RemoteVideoFrame
        {
            Buffer = buffer,
            Width = width,
            Height = height,
            Stride = stride
        });
    }

    private void StopCaptureAndReleaseResources()
    {
        lock (_captureSync)
        {
            if (_captureTimer is not null)
            {
                _captureTimer.Stop();
                _captureTimer.Elapsed -= OnCaptureTimerElapsed;
                _captureTimer.Dispose();
                _captureTimer = null;
            }

            _captureGraphics?.Dispose();
            _captureGraphics = null;
            _captureBitmap?.Dispose();
            _captureBitmap = null;
            if (_captureHandle.IsAllocated)
            {
                _captureHandle.Free();
            }
            _captureBuffer = null;
            _videoOptions = null;
        }
    }
}
