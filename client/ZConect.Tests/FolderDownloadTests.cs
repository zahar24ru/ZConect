using System.Text.Json;
using WebRtcTransport;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for folder download protocol payloads and FileRequest with RequestId.</summary>
public sealed class FolderDownloadTests
{
    [Fact]
    public void FileRequestPayload_includes_RequestId()
    {
        var payload = new FileRequestPayload { Path = @"C:\file.txt", RequestId = "req-abc" };
        var json = JsonSerializer.Serialize(payload);
        var back = JsonSerializer.Deserialize<FileRequestPayload>(json);
        Assert.NotNull(back);
        Assert.Equal(@"C:\file.txt", back.Path);
        Assert.Equal("req-abc", back.RequestId);
    }

    [Fact]
    public void FileRequestPayload_RequestId_defaults_to_empty()
    {
        var payload = new FileRequestPayload { Path = @"C:\file.txt" };
        Assert.Equal(string.Empty, payload.RequestId);
    }

    [Fact]
    public void FolderDownloadRequestPayload_serializes_correctly()
    {
        var payload = new FolderDownloadRequestPayload { Path = @"D:\MyFolder", RequestId = "folder-123" };
        var json = JsonSerializer.Serialize(payload);
        var back = JsonSerializer.Deserialize<FolderDownloadRequestPayload>(json);
        Assert.NotNull(back);
        Assert.Equal(@"D:\MyFolder", back.Path);
        Assert.Equal("folder-123", back.RequestId);
    }

    [Fact]
    public void FolderDownloadResponsePayload_success_serializes()
    {
        var payload = new FolderDownloadResponsePayload
        {
            RequestId = "folder-123",
            Success = true,
            FileCount = 42
        };
        var json = JsonSerializer.Serialize(payload);
        var back = JsonSerializer.Deserialize<FolderDownloadResponsePayload>(json);
        Assert.NotNull(back);
        Assert.Equal("folder-123", back.RequestId);
        Assert.True(back.Success);
        Assert.Equal(42, back.FileCount);
    }

    [Fact]
    public void FolderDownloadResponsePayload_failure_includes_message()
    {
        var payload = new FolderDownloadResponsePayload
        {
            RequestId = "folder-456",
            Success = false,
            Message = "Доступ запрещён"
        };
        var json = JsonSerializer.Serialize(payload);
        var back = JsonSerializer.Deserialize<FolderDownloadResponsePayload>(json);
        Assert.NotNull(back);
        Assert.False(back.Success);
        Assert.Equal("Доступ запрещён", back.Message);
        Assert.Equal(0, back.FileCount);
    }
}
