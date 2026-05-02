using System.IO;
using System.Text.Json;

namespace UiApp.Services;

public sealed class LogService
{
    public sealed class DebugLogFilterOptions
    {
        public bool DataChannelInputEnabled { get; set; } = true;
        public bool ClipboardEnabled { get; set; } = true;
        public bool SignalingEnabled { get; set; } = true;
        public bool WebRtcEnabled { get; set; } = true;
    }

    private readonly string _path;
    private readonly object _sync = new();
    private readonly object _filterSync = new();
    private DebugLogFilterOptions _debugFilter = new();

    private const long MaxLogSizeBytes = 5 * 1024 * 1024; // 5 MB
    private const int MaxBackupFiles = 3;

    public LogService()
    {
        // Write logs to C:\ProgramData\ZConect\logs\ — shared location writable by
        // both SYSTEM (service-spawned UI) and regular users. Previously used
        // %LocalAppData% which resolved to SYSTEM profile when launched by service,
        // making logs inaccessible at C:\Windows\system32\config\systemprofile\...
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ZConect", "logs");
        try { Directory.CreateDirectory(dir); } catch { }
        _path = Path.Combine(dir, "ui.log");
    }

    public void Info(string module, string message) => Write("INFO", module, message, null);
    public void Warn(string module, string message) => Write("WARN", module, message, null);
    public void Error(string module, string message, string? error = null) => Write("ERROR", module, message, error);
    public void Debug(string module, string message) => Write("DEBUG", module, message, null);

    public void SetDebugFilter(DebugLogFilterOptions options)
    {
        if (options is null)
        {
            return;
        }

        lock (_filterSync)
        {
            _debugFilter = new DebugLogFilterOptions
            {
                DataChannelInputEnabled = options.DataChannelInputEnabled,
                ClipboardEnabled = options.ClipboardEnabled,
                SignalingEnabled = options.SignalingEnabled,
                WebRtcEnabled = options.WebRtcEnabled
            };
        }
    }

    private void Write(string level, string module, string message, string? error)
    {
        if (string.Equals(level, "DEBUG", StringComparison.Ordinal) && !IsDebugAllowed(module, message))
        {
            return;
        }

        var payload = new
        {
            ts = DateTime.UtcNow.ToString("O"),
            level,
            module,
            event_name = message,
            error
        };

        var line = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        lock (_sync)
        {
            RotateIfNeeded();
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }

    private bool IsDebugAllowed(string module, string message)
    {
        var filter = ReadFilterSnapshot();

        // DataChannel Input noise: dc_sent/dc_recv input events.
        if (string.Equals(module, "DataChannel", StringComparison.Ordinal)
            && (message.StartsWith("dc_sent:Input:", StringComparison.Ordinal)
                || message.StartsWith("dc_recv:Input:", StringComparison.Ordinal)))
        {
            return filter.DataChannelInputEnabled;
        }

        // Clipboard noise: DataChannel clipboard envelopes + ClipboardSync events.
        if (string.Equals(module, "ClipboardSync", StringComparison.Ordinal)
            || (string.Equals(module, "DataChannel", StringComparison.Ordinal)
                && (message.StartsWith("dc_sent:Clipboard:", StringComparison.Ordinal)
                    || message.StartsWith("dc_recv:Clipboard:", StringComparison.Ordinal)
                    || message.StartsWith("clipboard_", StringComparison.Ordinal))))
        {
            return filter.ClipboardEnabled;
        }

        // Signaling and WS signaling notifications in UiApp.
        if (string.Equals(module, "Signaling", StringComparison.Ordinal)
            || (string.Equals(module, "UiApp", StringComparison.Ordinal)
                && message.StartsWith("ws_message_", StringComparison.Ordinal)))
        {
            return filter.SignalingEnabled;
        }

        // WebRTC debug stream.
        if (string.Equals(module, "WebRTC", StringComparison.Ordinal))
        {
            return filter.WebRtcEnabled;
        }

        // Any other debug modules are left untouched.
        return true;
    }

    private void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists || fi.Length < MaxLogSizeBytes) return;

            // Shift backup files: ui.3.log → delete, ui.2.log → ui.3.log, etc.
            for (int i = MaxBackupFiles; i >= 1; i--)
            {
                var src = Path.Combine(fi.DirectoryName!, $"ui.{i}.log");
                var dst = Path.Combine(fi.DirectoryName!, $"ui.{i + 1}.log");
                if (i == MaxBackupFiles && File.Exists(src))
                    File.Delete(src);
                else if (File.Exists(src))
                    File.Move(src, dst, overwrite: true);
            }
            File.Move(_path, Path.Combine(fi.DirectoryName!, "ui.1.log"), overwrite: true);
        }
        catch
        {
            // Rotation failure should not block logging.
        }
    }

    private DebugLogFilterOptions ReadFilterSnapshot()
    {
        lock (_filterSync)
        {
            return new DebugLogFilterOptions
            {
                DataChannelInputEnabled = _debugFilter.DataChannelInputEnabled,
                ClipboardEnabled = _debugFilter.ClipboardEnabled,
                SignalingEnabled = _debugFilter.SignalingEnabled,
                WebRtcEnabled = _debugFilter.WebRtcEnabled
            };
        }
    }
}
