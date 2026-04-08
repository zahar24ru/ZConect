namespace FileTransfer;

public enum TransferDirection
{
    Outgoing,
    Incoming
}

public enum TransferStatus
{
    Queued,
    InProgress,
    Paused,
    AwaitingAck,
    Completed,
    Failed,
    Cancelled
}

public sealed class TransferItem
{
    public string TransferId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public TransferDirection Direction { get; init; }
    public TransferStatus Status { get; set; }
    public long TotalBytes { get; init; }
    public long CurrentBytes { get; set; }
    public string? ErrorMessage { get; set; }

    public int ProgressPercent =>
        TotalBytes > 0 ? (int)Math.Clamp(CurrentBytes * 100 / TotalBytes, 0, 100) : 0;
}
