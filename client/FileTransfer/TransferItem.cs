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
    Unconfirmed, // UF-03: ACK timeout — sent but delivery not confirmed
    Completed,
    Failed,
    Cancelled
}

public sealed class TransferItem
{
    public string TransferId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    /// <summary>Full local file path (outgoing only, for retry).</summary>
    public string SourcePath { get; init; } = string.Empty;
    public TransferDirection Direction { get; init; }
    public TransferStatus Status { get; set; }
    public long TotalBytes { get; init; }
    public long CurrentBytes { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>Total files in the batch (from sender). 0 = unknown.</summary>
    public int BatchTotal { get; init; }
    /// <summary>Total bytes in the batch. 0 = unknown.</summary>
    public long BatchTotalBytes { get; init; }

    public int ProgressPercent =>
        TotalBytes > 0 ? (int)Math.Clamp(CurrentBytes * 100 / TotalBytes, 0, 100) : 0;
}
