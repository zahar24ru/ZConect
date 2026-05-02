using System.Runtime.InteropServices;

namespace UiApp.Services;

/// <summary>
/// Pre-flight check for default Windows audio endpoint sample rate.
/// mrwebrtc 2.0.2 (archived upstream) faststart-crashes with c0000409 FAST_FAIL_FATAL_APP_EXIT
/// when default audio device reports a mix format sample rate outside {8, 16, 24, 32, 44.1, 48, 96} kHz.
/// Confirmed 2026-04-15 — user with 192 kHz audio interface couldn't launch the app at all.
/// FAST_FAIL is a kernel-level abort, unrecoverable via managed try/catch — must pre-flight.
///
/// This helper queries WASAPI default render + capture endpoints and returns problematic rates
/// before PeerConnection init, so we can show a user-friendly dialog explaining how to fix it
/// in Windows Sound settings, instead of silent-crashing on startup.
/// </summary>
internal static class AudioPreflight
{
    private static readonly int[] SupportedRates = { 8000, 16000, 24000, 32000, 44100, 48000, 96000 };

    internal sealed record EndpointInfo(string Id, string FriendlyName, int SampleRate, bool IsSupported);
    internal sealed record Result(bool Ok, IReadOnlyList<EndpointInfo> Problematic, IReadOnlyList<EndpointInfo> All);

    /// <summary>Enumerate ALL active audio endpoints (both render + capture), not just defaults.
    /// mrwebrtc при PeerConnection init перечисляет все active endpoints через IMMDeviceEnumerator
    /// и падает на первом с unsupported sample rate (observed 2026-04-19: у user'а default devices
    /// были 48kHz но Steam virtual mic имел 192kHz → crash). Fail-open на exception внутри probe.</summary>
    public static Result Check()
    {
        var all = new List<EndpointInfo>();
        var problematic = new List<EndpointInfo>();
        try
        {
            EnumerateActiveEndpoints(eDataFlow: 0, all, problematic); // eRender (playback)
            EnumerateActiveEndpoints(eDataFlow: 1, all, problematic); // eCapture (microphones)
        }
        catch (Exception ex)
        {
            LogToUiLog("audio_preflight_probe_error", ex.Message);
            return new Result(true, Array.Empty<EndpointInfo>(), Array.Empty<EndpointInfo>());
        }

        foreach (var ep in all)
        {
            LogToUiLog("audio_preflight_endpoint",
                $"name=\"{ep.FriendlyName}\" rate={ep.SampleRate}Hz supported={ep.IsSupported}");
        }

        return new Result(problematic.Count == 0, problematic, all);
    }

    private static void EnumerateActiveEndpoints(int eDataFlow, List<EndpointInfo> all, List<EndpointInfo> problematic)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCoClass();
            // DEVICE_STATE_ACTIVE = 0x1 — только device'ы которые mrwebrtc реально попытается открыть.
            if (enumerator.EnumAudioEndpoints(eDataFlow, 0x1, out collection) != 0 || collection is null)
                return;
            if (collection.GetCount(out var count) != 0) return;

            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(i, out device) != 0 || device is null) continue;
                    var info = ProbeDevice(device);
                    if (info is null) continue;
                    all.Add(info);
                    if (!info.IsSupported) problematic.Add(info);
                }
                finally
                {
                    if (device is not null) Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            if (collection is not null) Marshal.ReleaseComObject(collection);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    private static EndpointInfo? ProbeDevice(IMMDevice device)
    {
        IAudioClient? audioClient = null;
        IntPtr pFormat = IntPtr.Zero;
        try
        {
            if (device.GetId(out var id) != 0 || string.IsNullOrEmpty(id)) return null;

            var friendly = TryGetFriendlyName(device) ?? ShortenDeviceId(id);

            var iid = typeof(IAudioClient).GUID;
            if (device.Activate(ref iid, 0x1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var clientObj) != 0 || clientObj is null)
                return null;
            audioClient = (IAudioClient)clientObj;

            if (audioClient.GetMixFormat(out pFormat) != 0 || pFormat == IntPtr.Zero) return null;
            var fmt = Marshal.PtrToStructure<WAVEFORMATEX>(pFormat);
            var rate = (int)fmt.nSamplesPerSec;
            var supported = Array.IndexOf(SupportedRates, rate) >= 0;
            return new EndpointInfo(id, friendly, rate, supported);
        }
        finally
        {
            if (pFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(pFormat);
            if (audioClient is not null) Marshal.ReleaseComObject(audioClient);
        }
    }

    /// <summary>Read PKEY_Device_FriendlyName через IPropertyStore → PROPVARIANT.
    /// Returns null if the property is missing or marshalling fails — caller falls back to GUID.
    /// IPropertyStore::GetValue записывает 24-байтный PROPVARIANT struct по переданному указателю —
    /// caller MUST передать `IntPtr pv` (указатель на свой буфер), а не `out IntPtr`. Ошибочное
    /// `out IntPtr` → stack corruption + access violation в coreclr exception handling
    /// (learned hard way в dump 2026-04-19 12:12:38). Buffer allocated 64 байта с запасом.</summary>
    private static string? TryGetFriendlyName(IMMDevice device)
    {
        IPropertyStore? props = null;
        IntPtr pvBuffer = IntPtr.Zero;
        try
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out props) != 0 || props is null)
                return null;

            pvBuffer = Marshal.AllocCoTaskMem(64);
            // Zero the buffer: иначе VT может быть прочитан из мусорных байт.
            for (var i = 0; i < 64; i += 8) Marshal.WriteInt64(pvBuffer, i, 0);

            var key = PKEY_Device_FriendlyName;
            if (props.GetValue(ref key, pvBuffer) != 0) return null;

            var vt = Marshal.ReadInt16(pvBuffer);
            if (vt != VT_LPWSTR) return null;

            // PROPVARIANT на x64: [0..1]=vt, [2..7]=reserved, [8..15]=union (pwszVal — LPWSTR).
            var strPtr = Marshal.ReadIntPtr(pvBuffer, 8);
            var friendly = strPtr != IntPtr.Zero ? Marshal.PtrToStringUni(strPtr) : null;

            // Освобождаем только LPWSTR через CoTaskMemFree (это владельческая строка от GetValue).
            if (strPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(strPtr);
            return string.IsNullOrWhiteSpace(friendly) ? null : friendly;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pvBuffer != IntPtr.Zero) Marshal.FreeCoTaskMem(pvBuffer);
            if (props is not null) Marshal.ReleaseComObject(props);
        }
    }

    private const short VT_LPWSTR = 31;

    // PKEY_Device_FriendlyName = { a45c254e-df1c-4efd-8020-67d146a850e0 }, pid=14.
    // Показывает дружественное имя, например "Speakers (Realtek Audio)" или "Steam Streaming Microphone".
    private static PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 14
    };

    /// <summary>Strip the well-known Windows audio device id prefix to leave just the GUID
    /// — fallback когда friendly name не удалось прочитать. Full id format:
    /// "{0.0.0.00000000}.{guid}" (render) or "{0.0.1.00000000}.{guid}" (capture).</summary>
    private static string ShortenDeviceId(string fullId)
    {
        var lastBrace = fullId.LastIndexOf('.');
        return lastBrace >= 0 && lastBrace + 1 < fullId.Length
            ? fullId[(lastBrace + 1)..]
            : fullId;
    }

    private static void LogToUiLog(string eventName, string details)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ZConect", "logs");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "ui.log");
            var line = $"{{\"ts\":\"{DateTime.UtcNow:o}\",\"level\":\"INFO\",\"module\":\"AudioPreflight\",\"event_name\":\"{eventName} {details}\",\"error\":null}}\n";
            System.IO.File.AppendAllText(path, line);
        }
        catch { /* best-effort */ }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCoClass { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore? ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        // CRITICAL: pv — IntPtr (caller-allocated buffer), НЕ out IntPtr.
        // Метод записывает 24-byte PROPVARIANT struct по указателю. out IntPtr → overwrite
        // stack beyond 8-byte variable → coreclr exception handler crashes (dump 2026-04-19).
        [PreserveSig] int GetValue(ref PROPERTYKEY key, IntPtr pv);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        int NotImpl_Initialize();
        int NotImpl_GetBufferSize();
        int NotImpl_GetStreamLatency();
        int NotImpl_GetCurrentPadding();
        int NotImpl_IsFormatSupported();
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }
}
