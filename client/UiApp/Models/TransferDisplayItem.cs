using System.ComponentModel;
using System.Runtime.CompilerServices;
using FileTransfer;

namespace UiApp.Models;

/// <summary>UI-model for a single file transfer displayed in the transfers list.</summary>
public sealed class TransferDisplayItem : INotifyPropertyChanged
{
    private TransferStatus _status;
    private long _currentBytes;
    private string? _errorMessage;
    private double _speedBytesPerSec;
    private string _speedText = "";
    private string _etaText = "";
    private long _lastBytes;
    private DateTime _lastTick = DateTime.UtcNow;

    public string TransferId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public TransferDirection Direction { get; init; }
    public long TotalBytes { get; init; }

    public TransferStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusColor)); OnPropertyChanged(nameof(IsActive)); }
    }

    public long CurrentBytes
    {
        get => _currentBytes;
        set { _currentBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressPercent)); OnPropertyChanged(nameof(ProgressValue)); OnPropertyChanged(nameof(ProgressText)); }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    public int ProgressPercent => TotalBytes > 0 ? (int)Math.Clamp(CurrentBytes * 100 / TotalBytes, 0, 100) : 0;
    public double ProgressValue => TotalBytes > 0 ? Math.Clamp((double)CurrentBytes / TotalBytes, 0, 1) : 0;

    public string ProgressText => TotalBytes > 0
        ? $"{FormatSize(CurrentBytes)} / {FormatSize(TotalBytes)} ({ProgressPercent}%)"
        : $"{FormatSize(CurrentBytes)}";

    public string SpeedText
    {
        get => _speedText;
        private set { _speedText = value; OnPropertyChanged(); }
    }

    public string EtaText
    {
        get => _etaText;
        private set { _etaText = value; OnPropertyChanged(); }
    }

    public string DirectionIcon => Direction == TransferDirection.Outgoing ? "↑" : "↓";
    public string DirectionText => Direction == TransferDirection.Outgoing ? "Отправка" : "Приём";

    public string StatusText => Status switch
    {
        TransferStatus.Queued => "В очереди",
        TransferStatus.InProgress => SpeedText,
        TransferStatus.Paused => "Пауза",
        TransferStatus.Completed => "Завершено",
        TransferStatus.Failed => ErrorMessage ?? "Ошибка",
        TransferStatus.Cancelled => "Отменено",
        _ => ""
    };

    public string StatusColor => Status switch
    {
        TransferStatus.InProgress => "#155FA2",
        TransferStatus.Completed => "#2E7D32",
        TransferStatus.Failed or TransferStatus.Cancelled => "#C62828",
        TransferStatus.Paused => "#F57F17",
        _ => "#666666"
    };

    public bool IsActive => Status is TransferStatus.InProgress or TransferStatus.Queued or TransferStatus.Paused;

    /// <summary>Call periodically or on each progress update to recalculate speed and ETA.</summary>
    public void UpdateSpeed()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastTick).TotalSeconds;
        if (elapsed < 0.3) return; // throttle

        var deltaBytes = CurrentBytes - _lastBytes;
        if (deltaBytes > 0 && elapsed > 0)
        {
            _speedBytesPerSec = _speedBytesPerSec > 0
                ? _speedBytesPerSec * 0.7 + (deltaBytes / elapsed) * 0.3 // EMA smoothing
                : deltaBytes / elapsed;
        }

        _lastBytes = CurrentBytes;
        _lastTick = now;

        SpeedText = _speedBytesPerSec > 0 ? $"{FormatSpeed(_speedBytesPerSec)}" : "";

        if (_speedBytesPerSec > 0 && TotalBytes > CurrentBytes && Status == TransferStatus.InProgress)
        {
            var remaining = (TotalBytes - CurrentBytes) / _speedBytesPerSec;
            EtaText = remaining < 60 ? $"~{(int)remaining}с"
                : remaining < 3600 ? $"~{(int)(remaining / 60)}м {(int)(remaining % 60)}с"
                : $"~{(int)(remaining / 3600)}ч {(int)(remaining % 3600 / 60)}м";
        }
        else
        {
            EtaText = "";
        }

        OnPropertyChanged(nameof(StatusText));
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
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
