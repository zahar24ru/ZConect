using System.Text.Json;
using WebRtcTransport;
using Xunit;

namespace ZConect.Tests;

/// <summary>Тесты для диагностики FT: RequestId, сериализация payload.</summary>
public sealed class FileTransferDiagnosticsTests
{
    [Fact]
    public void DirListResponse_accept_only_when_RequestId_matches_pending()
    {
        Assert.True(ShouldAcceptDirListResponse("abc", "abc"));
        Assert.False(ShouldAcceptDirListResponse("abc", "xyz"));
        Assert.False(ShouldAcceptDirListResponse("abc", ""));
        Assert.False(ShouldAcceptDirListResponse(null, "abc"));
        Assert.True(ShouldAcceptDirListResponse("", ""));
    }

    [Fact]
    public void DirListRequestPayload_serializes_RequestId()
    {
        var payload = new DirListRequestPayload { Path = "C:\\", RequestId = "req-123" };
        var json = JsonSerializer.Serialize(payload);
        var back = JsonSerializer.Deserialize<DirListRequestPayload>(json);
        Assert.NotNull(back);
        Assert.Equal("C:\\", back.Path);
        Assert.Equal("req-123", back.RequestId);
    }

    [Fact]
    public void DirListResponsePayload_serializes_RequestId_and_Items()
    {
        var payload = new DirListResponsePayload
        {
            Path = "C:\\",
            RequestId = "req-123",
            Items = new List<RemoteFileItemPayload>
            {
                new() { Name = "Users", FullPath = "C:\\Users", IsDirectory = true, Size = 0 }
            }
        };
        var json = JsonSerializer.Serialize(payload);
        var back = JsonSerializer.Deserialize<DirListResponsePayload>(json);
        Assert.NotNull(back);
        Assert.Equal("C:\\", back.Path);
        Assert.Equal("req-123", back.RequestId);
        Assert.Single(back.Items);
        Assert.Equal("Users", back.Items[0].Name);
        Assert.True(back.Items[0].IsDirectory);
    }

    /// <summary>Правило приёма ответа (как в MainViewModel): ответ принимаем только при совпадении RequestId.</summary>
    private static bool ShouldAcceptDirListResponse(string? pendingRequestId, string? responseRequestId)
    {
        return responseRequestId == pendingRequestId;
    }
}
