using System.Text.Json;

namespace ZConectService;

/// <summary>
/// Structured JSON logger for ZConect Windows Service.
/// Same format as client LogService — JSON Lines with rotation.
/// Logs to C:\ProgramData\ZConect\logs\service.log
/// </summary>
public sealed class ServiceLogger : IDisposable
{
    private readonly string _logPath;
    private readonly object _lock = new();
    private readonly long _maxFileSize;
    private readonly int _maxBackups;
    private StreamWriter? _writer;

    public ServiceLogger(string? logPath = null, long maxFileSizeBytes = 5 * 1024 * 1024, int maxBackups = 3)
    {
        var dir = logPath != null
            ? Path.GetDirectoryName(logPath)!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZConect", "logs");
        Directory.CreateDirectory(dir);
        _logPath = logPath ?? Path.Combine(dir, "service.log");
        _maxFileSize = maxFileSizeBytes;
        _maxBackups = maxBackups;
        OpenWriter();
    }

    public void Info(string module, string message) => Write("INFO", module, message);
    public void Warn(string module, string message) => Write("WARN", module, message);
    public void Error(string module, string message, string? error = null) => Write("ERROR", module, message, error);
    public void Debug(string module, string message) => Write("DEBUG", module, message);

    private void Write(string level, string module, string eventName, string? error = null)
    {
        var entry = new
        {
            ts = DateTime.UtcNow.ToString("O"),
            level,
            module,
            event_name = eventName,
            error
        };

        lock (_lock)
        {
            try
            {
                _writer?.WriteLine(JsonSerializer.Serialize(entry, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }));
                _writer?.Flush();
                RotateIfNeeded();
            }
            catch
            {
                // Never throw from logger — best effort.
            }
        }
    }

    private void OpenWriter()
    {
        try
        {
            _writer = new StreamWriter(_logPath, append: true) { AutoFlush = false };
        }
        catch
        {
            _writer = null;
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_logPath);
            if (!fi.Exists || fi.Length < _maxFileSize) return;

            _writer?.Close();
            _writer = null;

            // Shift backups: service.3.log → delete, service.2.log → service.3.log, etc.
            for (var i = _maxBackups; i >= 1; i--)
            {
                var src = i == 1 ? _logPath : $"{_logPath}.{i - 1}";
                var dst = $"{_logPath}.{i}";
                if (File.Exists(dst)) File.Delete(dst);
                if (File.Exists(src)) File.Move(src, dst);
            }

            OpenWriter();
        }
        catch { /* ignore rotation errors */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Close();
            _writer = null;
        }
    }
}
