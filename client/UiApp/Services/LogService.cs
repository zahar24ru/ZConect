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

    public LogService()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _path = Path.Combine(baseDir, "logs.log");
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

        var line = JsonSerializer.Serialize(payload);
        lock (_sync)
        {
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
