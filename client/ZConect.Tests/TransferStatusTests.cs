using FileTransfer;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for TransferStatus enum completeness and TransferItem behavior.</summary>
public sealed class TransferStatusTests
{
    [Fact]
    public void TransferStatus_has_Unconfirmed_value()
    {
        // UF-03: Unconfirmed status must exist for ACK timeout
        Assert.True(Enum.IsDefined(typeof(TransferStatus), TransferStatus.Unconfirmed));
    }

    [Fact]
    public void TransferItem_ProgressPercent_clamps_correctly()
    {
        var item = new TransferItem
        {
            TransferId = "t1",
            FileName = "test.txt",
            TotalBytes = 100,
            CurrentBytes = 150, // overflow
        };
        Assert.Equal(100, item.ProgressPercent);
    }

    [Fact]
    public void TransferItem_ProgressPercent_zero_total()
    {
        var item = new TransferItem { TotalBytes = 0, CurrentBytes = 50 };
        Assert.Equal(0, item.ProgressPercent);
    }

    [Theory]
    [InlineData("../malicious", true)]
    [InlineData("subfolder/normal.txt", false)]
    [InlineData(@"C:\absolute", true)]
    [InlineData(@"\\unc\path", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SafePath_IsDangerous_for_relative_paths(string? path, bool expected)
    {
        Assert.Equal(expected, SafePath.IsDangerous(path));
    }
}
