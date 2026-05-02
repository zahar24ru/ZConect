using ZConectService;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for the Windows Service structured logger.</summary>
public sealed class ServiceLoggerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logPath;

    public ServiceLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ZConectTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _logPath = Path.Combine(_tempDir, "test-service.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Logger_creates_log_file()
    {
        using var logger = new ServiceLogger(_logPath);
        logger.Info("Test", "hello_world");

        Assert.True(File.Exists(_logPath));
    }

    [Fact]
    public void Logger_writes_json_lines()
    {
        using var logger = new ServiceLogger(_logPath);
        logger.Info("Module1", "event_one");
        logger.Warn("Module2", "event_two");
        logger.Error("Module3", "event_three", "some_error");

        logger.Dispose(); // flush

        var lines = File.ReadAllLines(_logPath);
        Assert.Equal(3, lines.Length);

        // Each line should be valid JSON with expected fields.
        foreach (var line in lines)
        {
            Assert.Contains("\"ts\":", line);
            Assert.Contains("\"level\":", line);
            Assert.Contains("\"module\":", line);
            Assert.Contains("\"event_name\":", line);
        }
    }

    [Fact]
    public void Logger_includes_correct_levels()
    {
        using var logger = new ServiceLogger(_logPath);
        logger.Info("M", "info_event");
        logger.Warn("M", "warn_event");
        logger.Error("M", "error_event");
        logger.Debug("M", "debug_event");
        logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("\"level\":\"INFO\"", content);
        Assert.Contains("\"level\":\"WARN\"", content);
        Assert.Contains("\"level\":\"ERROR\"", content);
        Assert.Contains("\"level\":\"DEBUG\"", content);
    }

    [Fact]
    public void Logger_includes_error_field()
    {
        using var logger = new ServiceLogger(_logPath);
        logger.Error("M", "fail", "connection_refused");
        logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("connection_refused", content);
    }

    [Fact]
    public void Logger_rotates_on_size_limit()
    {
        // Use tiny max size to force rotation.
        using var logger = new ServiceLogger(_logPath, maxFileSizeBytes: 200, maxBackups: 2);

        for (int i = 0; i < 50; i++)
            logger.Info("Rot", $"line_{i}_padding_to_fill_buffer_xxxxxxxxxxxxxxxxx");

        logger.Dispose();

        // Should have rotated — backup file should exist.
        Assert.True(File.Exists(_logPath)); // current log
        Assert.True(File.Exists($"{_logPath}.1")); // at least one backup
    }

    [Fact]
    public async Task Logger_is_thread_safe()
    {
        using var logger = new ServiceLogger(_logPath);

        // Write from 10 threads concurrently.
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Task.Run(() =>
            {
                for (int j = 0; j < 20; j++)
                    logger.Info("Thread", $"t{i}_msg{j}");
            })).ToArray();

        await Task.WhenAll(tasks);
        logger.Dispose();

        var lines = File.ReadAllLines(_logPath);
        Assert.Equal(200, lines.Length);
    }
}
