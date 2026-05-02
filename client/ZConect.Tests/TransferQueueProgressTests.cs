using UiApp.Models;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for aggregated transfer progress (Windows Explorer-style).</summary>
public sealed class TransferQueueProgressTests
{
    [Fact]
    public void Initial_state_is_inactive()
    {
        var p = new TransferQueueProgress();
        Assert.False(p.IsActive);
        Assert.Equal(0, p.TotalFiles);
        Assert.Equal(0, p.CompletedFiles);
        Assert.Equal(0, p.OverallPercent);
        Assert.False(p.ShowSummary);
    }

    [Fact]
    public void EnqueueFiles_activates_progress()
    {
        var p = new TransferQueueProgress();
        p.EnqueueFiles(5, 1024 * 1024);

        Assert.True(p.IsActive);
        Assert.Equal(5, p.TotalFiles);
        Assert.Equal(1024 * 1024, p.TotalBytes);
    }

    [Fact]
    public void FileCompleted_increments_counter()
    {
        var p = new TransferQueueProgress();
        p.EnqueueFiles(3, 3000);

        p.FileCompleted(1000);
        Assert.Equal(1, p.CompletedFiles);
        Assert.Equal(1000, p.TransferredBytes);
        Assert.True(p.IsActive); // still 2 left
    }

    [Fact]
    public void FileFailed_increments_counter()
    {
        var p = new TransferQueueProgress();
        p.EnqueueFiles(2, 2000);

        p.FileFailed();
        Assert.Equal(1, p.FailedFiles);
    }

    [Fact]
    public void All_complete_shows_summary_after_delay()
    {
        var p = new TransferQueueProgress();
        p.EnqueueFiles(1, 500);
        p.FileCompleted(500);

        // IsActive stays true for 600ms after last file to prevent progress bar flicker.
        Assert.True(p.IsActive);
        Thread.Sleep(700);
        Assert.False(p.IsActive);
    }

    [Fact]
    public void OverallProgress_calculation()
    {
        var p = new TransferQueueProgress();
        p.EnqueueFiles(2, 1000);
        p.UpdateCurrentFileProgress("file.txt", 250, 500);

        Assert.Equal(250, p.TransferredBytes);
        Assert.InRange(p.OverallProgress, 0.24, 0.26); // ~25%
        Assert.Equal(25, p.OverallPercent);
    }

    [Fact]
    public void OverallProgress_zero_total()
    {
        var p = new TransferQueueProgress();
        Assert.Equal(0.0, p.OverallProgress);
        Assert.Equal(0, p.OverallPercent);
    }

    [Fact]
    public void StatusText_format()
    {
        var p = new TransferQueueProgress();
        p.EnqueueFiles(10, 10000);
        p.FileCompleted(1000);

        // "Файл 2 из 10" (file 2 of 10, since 1 completed + 1 current)
        Assert.Contains("из 10", p.StatusText);
    }

    [Fact]
    public void Reset_clears_everything()
    {
        var p = new TransferQueueProgress();
        p.EnqueueFiles(5, 5000);
        p.FileCompleted(1000);
        p.FileCompleted(1000);

        p.Reset();

        Assert.Equal(0, p.TotalFiles);
        Assert.Equal(0, p.CompletedFiles);
        Assert.Equal(0, p.TransferredBytes);
        Assert.Equal(0, p.TotalBytes);
        Assert.False(p.IsActive);
        Assert.False(p.ShowSummary);
        Assert.Equal(string.Empty, p.CurrentFileName);
    }

    [Fact]
    public void CurrentFileName_updated_on_progress()
    {
        var p = new TransferQueueProgress();
        p.EnqueueFiles(1, 1000);
        p.UpdateCurrentFileProgress("document.pdf", 500, 1000);

        Assert.Equal("document.pdf", p.CurrentFileName);
    }

    [Fact]
    public void ProgressText_format()
    {
        var p = new TransferQueueProgress();
        p.EnqueueFiles(1, 1024 * 1024); // 1 MB
        p.UpdateCurrentFileProgress("f.bin", 512 * 1024, 1024 * 1024); // 512 KB

        Assert.Contains("KB", p.ProgressText);
        Assert.Contains("/", p.ProgressText);
    }

    [Fact]
    public void Multiple_enqueue_accumulates()
    {
        var p = new TransferQueueProgress();
        p.EnqueueFiles(3, 3000);
        p.EnqueueFiles(2, 2000);

        Assert.Equal(5, p.TotalFiles);
        Assert.Equal(5000, p.TotalBytes);
    }

    [Fact]
    public void IsPaused_property()
    {
        var p = new TransferQueueProgress();
        Assert.False(p.IsPaused);
        p.IsPaused = true;
        Assert.True(p.IsPaused);
    }
}
