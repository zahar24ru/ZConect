using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenCapture;

/// <summary>
/// High-performance screen capture using DXGI Output Duplication API.
/// Falls back gracefully: caller should catch exceptions and use GDI fallback.
/// </summary>
public sealed class DxgiScreenCapture : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private int _width;
    private int _height;
    private bool _disposed;
    private bool _frameAcquired;

    // ── Dirty rects ──
    private OutduplFrameInfo _lastFrameInfo;
    private RawRect[] _dirtyRectsBuffer = new RawRect[64];
    private int _dirtyRectCount;

    /// <summary>Width of the captured output.</summary>
    public int Width => _width;

    /// <summary>Height of the captured output.</summary>
    public int Height => _height;

    /// <summary>
    /// Initialize DXGI capture for a specific display output.
    /// </summary>
    public void Initialize(int adapterIndex = 0, int outputIndex = 0)
    {
        Cleanup();

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1((uint)adapterIndex, out var adapter);

        D3D11.D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
            out _device,
            out _context);

        if (_device is null || _context is null)
        {
            adapter.Dispose();
            throw new InvalidOperationException("Failed to create D3D11 device.");
        }

        adapter.EnumOutputs((uint)outputIndex, out var output);
        adapter.Dispose();

        using var output1 = output.QueryInterface<IDXGIOutput1>();
        var desc = output.Description;
        output.Dispose();

        _width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
        _height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

        _duplication = output1.DuplicateOutput(_device);

        // Create staging texture for CPU read-back.
        var texDesc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None
        };
        _stagingTexture = _device.CreateTexture2D(texDesc);
    }

    /// <summary>
    /// Initialize DXGI capture for a display matching the given screen coordinates.
    /// </summary>
    public void InitializeForRegion(int captureX, int captureY)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint ai = 0; ai < 8; ai++)
        {
            if (factory.EnumAdapters1(ai, out var adapter).Failure)
            {
                adapter?.Dispose();
                break;
            }
            using (adapter)
            {
                for (uint oi = 0; oi < 8; oi++)
                {
                    if (adapter.EnumOutputs(oi, out var output).Failure)
                    {
                        output?.Dispose();
                        break;
                    }
                    using (output)
                    {
                        var r = output.Description.DesktopCoordinates;
                        if (captureX >= r.Left && captureX < r.Right && captureY >= r.Top && captureY < r.Bottom)
                        {
                            Initialize((int)ai, (int)oi);
                            return;
                        }
                    }
                }
            }
        }

        // Fallback: primary output.
        Initialize(0, 0);
    }

    /// <summary>
    /// Try to acquire a new frame. Returns true if a new frame is available.
    /// </summary>
    /// <summary>Number of dirty rects in the last acquired frame.</summary>
    public int DirtyRectCount => _dirtyRectCount;

    /// <summary>Total pixel area covered by dirty rects.</summary>
    public int DirtyRectPixelArea { get; private set; }

    public bool TryAcquireFrame(int timeoutMs = 0)
    {
        if (_duplication is null)
            throw new InvalidOperationException("DXGI capture not initialized.");

        if (_frameAcquired)
        {
            _duplication.ReleaseFrame();
            _frameAcquired = false;
        }

        _dirtyRectCount = 0;
        DirtyRectPixelArea = 0;

        var result = _duplication.AcquireNextFrame((uint)timeoutMs, out _lastFrameInfo, out var resource);

        if (result.Failure)
        {
            resource?.Dispose();
            return false;
        }

        // Copy desktop texture to staging texture.
        using (resource)
        using (var srcTexture = resource!.QueryInterface<ID3D11Texture2D>())
        {
            _context!.CopyResource(_stagingTexture!, srcTexture);
        }

        _frameAcquired = true;

        // Extract dirty rects
        if (_lastFrameInfo.TotalMetadataBufferSize > 0)
        {
            try
            {
                var bufSize = (uint)(_dirtyRectsBuffer.Length * Marshal.SizeOf<RawRect>());
                var hr = _duplication.GetFrameDirtyRects(bufSize, _dirtyRectsBuffer, out var requiredSize);
                if (hr.Success)
                {
                    _dirtyRectCount = (int)(requiredSize / (uint)Marshal.SizeOf<RawRect>());
                    var area = 0;
                    for (var i = 0; i < _dirtyRectCount; i++)
                    {
                        ref var r = ref _dirtyRectsBuffer[i];
                        area += (r.Right - r.Left) * (r.Bottom - r.Top);
                    }
                    DirtyRectPixelArea = area;
                }
                else if (requiredSize > bufSize)
                {
                    // Buffer too small — dirty rects cover many regions, treat as full frame
                    _dirtyRectCount = 0;
                    DirtyRectPixelArea = _width * _height;
                }
            }
            catch
            {
                _dirtyRectCount = 0;
                DirtyRectPixelArea = _width * _height;
            }
        }

        return true;
    }

    /// <summary>
    /// Copy the captured frame pixels into the destination buffer (BGRA32 format).
    /// </summary>
    /// <param name="dest">Destination buffer, must be at least Width * Height * 4 bytes.</param>
    /// <returns>Stride (bytes per row) of the captured frame.</returns>
    public int CopyFrameTo(byte[] dest)
    {
        if (_stagingTexture is null || _context is null)
            throw new InvalidOperationException("No frame available.");

        var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
        try
        {
            var srcStride = (int)mapped.RowPitch;
            var dstStride = _width * 4;
            var srcPtr = mapped.DataPointer;

            for (var y = 0; y < _height; y++)
            {
                Marshal.Copy(IntPtr.Add(srcPtr, y * srcStride), dest, y * dstStride, dstStride);
            }

            return dstStride;
        }
        finally
        {
            _context.Unmap(_stagingTexture, 0);
        }
    }

    /// <summary>
    /// Copy only the dirty rect regions from the captured frame into the destination buffer.
    /// Assumes dest already contains the previous frame's data for non-dirty areas.
    /// </summary>
    public void CopyDirtyRectsTo(byte[] dest)
    {
        if (_stagingTexture is null || _context is null || _dirtyRectCount <= 0)
            return;

        var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
        try
        {
            var srcStride = (int)mapped.RowPitch;
            var dstStride = _width * 4;
            var srcPtr = mapped.DataPointer;

            for (var i = 0; i < _dirtyRectCount; i++)
            {
                ref var r = ref _dirtyRectsBuffer[i];
                var left = Math.Max(0, r.Left);
                var top = Math.Max(0, r.Top);
                var right = Math.Min(_width, r.Right);
                var bottom = Math.Min(_height, r.Bottom);
                var rowBytes = (right - left) * 4;
                if (rowBytes <= 0) continue;

                for (var y = top; y < bottom; y++)
                {
                    var srcOffset = y * srcStride + left * 4;
                    var dstOffset = y * dstStride + left * 4;
                    Marshal.Copy(IntPtr.Add(srcPtr, srcOffset), dest, dstOffset, rowBytes);
                }
            }
        }
        finally
        {
            _context.Unmap(_stagingTexture, 0);
        }
    }

    /// <summary>Release the acquired frame.</summary>
    public void ReleaseFrame()
    {
        if (_frameAcquired && _duplication is not null)
        {
            _duplication.ReleaseFrame();
            _frameAcquired = false;
        }
    }

    private void Cleanup()
    {
        if (_frameAcquired && _duplication is not null)
        {
            try { _duplication.ReleaseFrame(); } catch { }
            _frameAcquired = false;
        }

        _stagingTexture?.Dispose();
        _duplication?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _stagingTexture = null;
        _duplication = null;
        _context = null;
        _device = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }
}
