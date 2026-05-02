using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenCapture;

/// <summary>
/// Captures all monitors via DXGI Output Duplication and stitches them into a single buffer.
/// Each output is captured independently; frames are composited by desktop coordinates.
/// </summary>
public sealed class DxgiMultiOutputCapture : IDisposable
{
    private sealed class OutputSlot : IDisposable
    {
        public ID3D11Device? Device;
        public ID3D11DeviceContext? Context;
        public IDXGIOutputDuplication? Duplication;
        public ID3D11Texture2D? StagingTexture;
        public int X, Y, W, H; // desktop coordinates
        public bool FrameAcquired;

        public void Dispose()
        {
            if (FrameAcquired && Duplication is not null)
            {
                try { Duplication.ReleaseFrame(); } catch { }
                FrameAcquired = false;
            }
            StagingTexture?.Dispose();
            Duplication?.Dispose();
            Context?.Dispose();
            Device?.Dispose();
        }
    }

    private OutputSlot[]? _slots;
    private int _totalWidth;
    private int _totalHeight;
    private int _originX;
    private int _originY;
    private bool _disposed;

    public int Width => _totalWidth;
    public int Height => _totalHeight;

    /// <summary>
    /// Initialize capture for all outputs across all adapters.
    /// </summary>
    public void Initialize()
    {
        Dispose();
        _disposed = false;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var slots = new List<OutputSlot>();

        for (uint ai = 0; ai < 8; ai++)
        {
            if (factory.EnumAdapters1(ai, out var adapter).Failure)
            {
                adapter?.Dispose();
                break;
            }

            for (uint oi = 0; oi < 8; oi++)
            {
                if (adapter.EnumOutputs(oi, out var output).Failure)
                {
                    output?.Dispose();
                    break;
                }

                try
                {
                    // Each output needs its own device (or shared device on same adapter)
                    ID3D11Device? device;
                    ID3D11DeviceContext? context;
                    D3D11.D3D11CreateDevice(
                        adapter,
                        DriverType.Unknown,
                        DeviceCreationFlags.BgraSupport,
                        new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
                        out device,
                        out context);

                    if (device is null || context is null)
                    {
                        device?.Dispose();
                        context?.Dispose();
                        output.Dispose();
                        continue;
                    }

                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    var desc = output.Description;
                    output.Dispose();

                    var w = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                    var h = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

                    var duplication = output1.DuplicateOutput(device);

                    var texDesc = new Texture2DDescription
                    {
                        Width = (uint)w,
                        Height = (uint)h,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        BindFlags = BindFlags.None
                    };
                    var staging = device.CreateTexture2D(texDesc);

                    slots.Add(new OutputSlot
                    {
                        Device = device,
                        Context = context,
                        Duplication = duplication,
                        StagingTexture = staging,
                        X = desc.DesktopCoordinates.Left,
                        Y = desc.DesktopCoordinates.Top,
                        W = w,
                        H = h
                    });
                }
                catch
                {
                    output.Dispose();
                }
            }

            adapter.Dispose();
        }

        if (slots.Count == 0)
            throw new InvalidOperationException("No DXGI outputs found.");

        _slots = slots.ToArray();

        _originX = _slots.Min(s => s.X);
        _originY = _slots.Min(s => s.Y);
        _totalWidth = _slots.Max(s => s.X + s.W) - _originX;
        _totalHeight = _slots.Max(s => s.Y + s.H) - _originY;
    }

    /// <summary>DXGI_ERROR_ACCESS_LOST — desktop switched (UAC, lock, Ctrl+Alt+Del).</summary>
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);

    /// <summary>
    /// Try to acquire frames from all outputs. Returns true if at least one output has a new frame.
    /// </summary>
    public bool TryAcquireFrames(int timeoutMs = 0)
    {
        var r = TryAcquireFramesEx(timeoutMs);
        return r == AcquireResult.Success;
    }

    /// <summary>
    /// Try to acquire frames with detailed result.
    /// Returns <see cref="AcquireResult.AccessLost"/> when ANY output reports desktop switch.
    /// </summary>
    public AcquireResult TryAcquireFramesEx(int timeoutMs = 0)
    {
        if (_slots is null) throw new InvalidOperationException("Not initialized.");

        var anyNew = false;
        var anyAccessLost = false;

        foreach (var slot in _slots)
        {
            if (slot.FrameAcquired)
            {
                slot.Duplication!.ReleaseFrame();
                slot.FrameAcquired = false;
            }

            var result = slot.Duplication!.AcquireNextFrame((uint)timeoutMs, out _, out var resource);
            if (result.Failure)
            {
                resource?.Dispose();
                if (result.Code == DXGI_ERROR_ACCESS_LOST)
                    anyAccessLost = true;
                continue;
            }

            using (resource)
            using (var srcTex = resource!.QueryInterface<ID3D11Texture2D>())
            {
                slot.Context!.CopyResource(slot.StagingTexture!, srcTex);
            }

            slot.FrameAcquired = true;
            anyNew = true;
        }

        if (anyAccessLost) return AcquireResult.AccessLost;
        return anyNew ? AcquireResult.Success : AcquireResult.NoFrame;
    }

    /// <summary>
    /// Composite all captured outputs into a single BGRA32 buffer.
    /// Buffer must be at least Width * Height * 4 bytes.
    /// </summary>
    public void CompositeFramesTo(byte[] dest)
    {
        if (_slots is null) throw new InvalidOperationException("Not initialized.");

        var dstStride = _totalWidth * 4;

        foreach (var slot in _slots)
        {
            if (!slot.FrameAcquired || slot.StagingTexture is null || slot.Context is null)
                continue;

            var mapped = slot.Context.Map(slot.StagingTexture, 0, MapMode.Read);
            try
            {
                var srcStride = (int)mapped.RowPitch;
                var srcPtr = mapped.DataPointer;
                var slotOffX = slot.X - _originX;
                var slotOffY = slot.Y - _originY;
                var rowBytes = slot.W * 4;

                for (var y = 0; y < slot.H; y++)
                {
                    var dstOffset = (slotOffY + y) * dstStride + slotOffX * 4;
                    Marshal.Copy(IntPtr.Add(srcPtr, y * srcStride), dest, dstOffset, rowBytes);
                }
            }
            finally
            {
                slot.Context.Unmap(slot.StagingTexture, 0);
            }
        }
    }

    /// <summary>Release all acquired frames.</summary>
    public void ReleaseFrames()
    {
        if (_slots is null) return;
        foreach (var slot in _slots)
        {
            if (slot.FrameAcquired && slot.Duplication is not null)
            {
                slot.Duplication.ReleaseFrame();
                slot.FrameAcquired = false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_slots is not null)
        {
            foreach (var slot in _slots)
                slot.Dispose();
            _slots = null;
        }
    }
}
