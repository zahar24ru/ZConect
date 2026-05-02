using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace UiApp.Models;

/// <summary>Aggregated transfer queue progress — Windows Explorer copy-style.</summary>
public sealed class TransferQueueProgress : INotifyPropertyChanged
{
    private int _totalFiles;
    private int _completedFiles;
    private int _failedFiles;
    private string _currentFileName = string.Empty;
    private long _totalBytes;
    private long _transferredBytes;
    private long _currentFileTransferred;
    private long _completedBytes; // bytes from fully completed files
    private bool _isPaused;
    private double _speedBytesPerSec;
    private string _speedText = string.Empty;
    private string _etaText = string.Empty;
    private long _lastBytes;
    private DateTime _lastTick = DateTime.UtcNow;
    private string _summaryText = string.Empty;
    private bool _showSummary;

    public int TotalFiles { get => _totalFiles; set { _totalFiles = value; Notify(); Notify(nameof(StatusText)); Notify(nameof(IsActive)); } }
    public int CompletedFiles { get => _completedFiles; set { _completedFiles = value; Notify(); Notify(nameof(StatusText)); } }
    public int FailedFiles { get => _failedFiles; set { _failedFiles = value; Notify(); } }

    public string CurrentFileName { get => _currentFileName; set { _currentFileName = value; Notify(); } }

    public long TotalBytes { get => _totalBytes; set { _totalBytes = value; Notify(); Notify(nameof(OverallProgress)); Notify(nameof(OverallPercent)); Notify(nameof(ProgressText)); } }
    public long TransferredBytes { get => _transferredBytes; set { _transferredBytes = value; Notify(); Notify(nameof(OverallProgress)); Notify(nameof(OverallPercent)); Notify(nameof(ProgressText)); } }

    public double OverallProgress => TotalBytes > 0 ? Math.Clamp((double)TransferredBytes / TotalBytes, 0, 1) : 0;
    public int OverallPercent => (int)(OverallProgress * 100);
    public string ProgressText => TotalBytes > 0
        ? $"{FormatSize(TransferredBytes)} / {FormatSize(TotalBytes)}"
        : $"{FormatSize(TransferredBytes)}";

    public string StatusText
    {
        get
        {
            if (TotalFiles == 0) return string.Empty;
            if (IsAwaitingAck) return "Ожидание подтверждения получателем…";
            var fileNum = CompletedFiles + 1;
            if (fileNum > TotalFiles) fileNum = TotalFiles;
            return $"Файл {fileNum} из {TotalFiles}";
        }
    }

    private bool _isAwaitingAck;
    /// <summary>True пока хотя бы один outgoing transfer находится в AwaitingAck —
    /// все chunks ушли в SCTP, ждём file_ack от receiver'а (hash verify + move из .part).
    /// Без этого флага UI показывал 100% и "Файл 1 из 1" одновременно, пользователь
    /// думал что уже готово, а приём ещё писался на диск.</summary>
    public bool IsAwaitingAck
    {
        get => _isAwaitingAck;
        set
        {
            if (_isAwaitingAck == value) return;
            _isAwaitingAck = value;
            Notify();
            Notify(nameof(StatusText));
        }
    }

    public string SpeedText { get => _speedText; private set { _speedText = value; Notify(); } }
    public string EtaText { get => _etaText; private set { _etaText = value; Notify(); } }

    /// <summary>True while transfer is in progress. Stays true for 600ms after last file
    /// to prevent progress bar flicker between sequential files in a batch.</summary>
    public bool IsActive => TotalFiles > 0 && ((CompletedFiles + FailedFiles) < TotalFiles || _keepActiveUntil > DateTime.UtcNow);
    private DateTime _keepActiveUntil;
    public bool IsPaused { get => _isPaused; set { _isPaused = value; Notify(); } }

    public string SummaryText { get => _summaryText; set { _summaryText = value; Notify(); } }
    public bool ShowSummary { get => _showSummary; set { _showSummary = value; Notify(); } }

    public ICommand? PauseAllCommand { get; init; }
    public ICommand? ResumeAllCommand { get; init; }
    public ICommand? CancelAllCommand { get; init; }

    /// <summary>Update progress for the currently active file.</summary>
    public void UpdateCurrentFileProgress(string fileName, long currentBytes, long totalBytes)
    {
        CurrentFileName = fileName;
        _currentFileTransferred = currentBytes;
        TransferredBytes = _completedBytes + currentBytes;

        // Update total if we got more info (incoming files report size in meta)
        if (TotalBytes < _completedBytes + totalBytes)
            TotalBytes = _completedBytes + totalBytes;
    }

    private System.Threading.Timer? _summaryTimer;

    /// <summary>Mark a file as completed.</summary>
    public void FileCompleted(long fileBytes)
    {
        _completedBytes += fileBytes;
        TransferredBytes = _completedBytes;
        CompletedFiles++;
        _currentFileTransferred = 0;
        IsAwaitingAck = false;
        // Keep IsActive=true for 600ms so progress bar doesn't flicker between files.
        _keepActiveUntil = DateTime.UtcNow.AddMilliseconds(600);

        // Check real completion (ignoring _keepActiveUntil grace period).
        var reallyDone = (CompletedFiles + FailedFiles) >= TotalFiles;
        if (reallyDone)
        {
            // Delay summary by 700ms (after _keepActiveUntil expires) — then show results.
            _summaryTimer?.Dispose();
            _summaryTimer = new System.Threading.Timer(_ =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (!ShowSummary)
                    {
                        var failed = FailedFiles > 0 ? $", ошибок: {FailedFiles}" : "";
                        SummaryText = $"Передано {CompletedFiles} файл(ов), {FormatSize(_completedBytes)}{failed}";
                        ShowSummary = true;
                        SpeedText = string.Empty;
                        EtaText = string.Empty;
                        CurrentFileName = string.Empty;
                    }
                });
            }, null, 700, System.Threading.Timeout.Infinite); // 700ms > 600ms _keepActiveUntil
        }
        Notify(nameof(IsActive));
    }

    /// <summary>Mark a file as failed.</summary>
    public void FileFailed()
    {
        FailedFiles++;
        if (!IsActive)
        {
            SummaryText = $"Передано {CompletedFiles}, ошибок: {FailedFiles}";
            ShowSummary = true;
        }
        Notify(nameof(IsActive));
    }

    /// <summary>Add files to the queue (call when new files are enqueued).</summary>
    public void EnqueueFiles(int count, long totalBytes)
    {
        // Auto-reset when previous batch is complete (prevents TotalFiles accumulation).
        if (!IsActive && TotalFiles > 0)
        {
            _totalFiles = 0; _completedFiles = 0; _failedFiles = 0;
            _totalBytes = 0; _transferredBytes = 0; _completedBytes = 0; _currentFileTransferred = 0;
        }

        TotalFiles += count;
        TotalBytes += totalBytes;
        ShowSummary = false;
        Notify(nameof(IsActive));
    }

    /// <summary>Reset for new batch.</summary>
    public void Reset()
    {
        _summaryTimer?.Dispose();
        _summaryTimer = null;
        _totalFiles = 0; _completedFiles = 0; _failedFiles = 0;
        _totalBytes = 0; _transferredBytes = 0; _completedBytes = 0; _currentFileTransferred = 0;
        _currentFileName = string.Empty;
        _isPaused = false; _speedBytesPerSec = 0;
        _speedText = string.Empty; _etaText = string.Empty;
        _summaryText = string.Empty; _showSummary = false;
        _isAwaitingAck = false;
        _lastBytes = 0; _lastTick = DateTime.UtcNow; _keepActiveUntil = DateTime.MinValue;
        Notify(string.Empty); // refresh all
    }

    /// <summary>Recalculate speed and ETA (call periodically).</summary>
    public void UpdateSpeed()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastTick).TotalSeconds;
        if (elapsed < 0.3) return;

        var delta = TransferredBytes - _lastBytes;
        if (delta > 0 && elapsed > 0)
        {
            _speedBytesPerSec = _speedBytesPerSec > 0
                ? _speedBytesPerSec * 0.7 + (delta / elapsed) * 0.3
                : delta / elapsed;
        }
        _lastBytes = TransferredBytes;
        _lastTick = now;

        SpeedText = _speedBytesPerSec > 0 ? FormatSpeed(_speedBytesPerSec) : string.Empty;

        if (_speedBytesPerSec > 0 && TotalBytes > TransferredBytes && IsActive)
        {
            var remaining = (TotalBytes - TransferredBytes) / _speedBytesPerSec;
            EtaText = remaining < 60 ? $"~{(int)remaining}с"
                : remaining < 3600 ? $"~{(int)(remaining / 60)}м {(int)(remaining % 60)}с"
                : $"~{(int)(remaining / 3600)}ч {(int)(remaining % 3600 / 60)}м";
        }
        else
        {
            EtaText = string.Empty;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:F1} KB/s";
        return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
