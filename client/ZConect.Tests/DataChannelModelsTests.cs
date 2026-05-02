using System.Text.Json;
using WebRtcTransport;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for data channel payload serialization (all message types).</summary>
public sealed class DataChannelModelsTests
{
    private static T Roundtrip<T>(T obj) where T : class
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<T>(json)!;
    }

    [Fact]
    public void FileMetaPayload_roundtrip()
    {
        var p = new FileMetaPayload
        {
            TransferId = "abc123", FileName = "doc.pdf", FileSize = 1024 * 1024,
            Hash = "SHA256HASH", TargetDirectory = @"C:\Downloads", RelativePath = @"sub\doc.pdf",
            Compressed = true
        };
        var r = Roundtrip(p);
        Assert.Equal("abc123", r.TransferId);
        Assert.Equal("doc.pdf", r.FileName);
        Assert.Equal(1024 * 1024, r.FileSize);
        Assert.Equal(@"sub\doc.pdf", r.RelativePath);
        Assert.True(r.Compressed);
    }

    [Fact]
    public void FileAckPayload_success_and_failure()
    {
        var ok = Roundtrip(new FileAckPayload { TransferId = "t1", Success = true });
        Assert.True(ok.Success);

        var fail = Roundtrip(new FileAckPayload { TransferId = "t2", Success = false, Message = "Hash mismatch" });
        Assert.False(fail.Success);
        Assert.Equal("Hash mismatch", fail.Message);
    }

    [Fact]
    public void FileErrorPayload_codes()
    {
        var p = Roundtrip(new FileErrorPayload { TransferId = "t1", Code = "not_found", Message = "File not found" });
        Assert.Equal("not_found", p.Code);
        Assert.Equal("File not found", p.Message);
    }

    [Fact]
    public void RenameRequestPayload_roundtrip()
    {
        var p = Roundtrip(new RenameRequestPayload { Path = @"C:\old.txt", NewName = "new.txt", RequestId = "r1" });
        Assert.Equal(@"C:\old.txt", p.Path);
        Assert.Equal("new.txt", p.NewName);
        Assert.Equal("r1", p.RequestId);
    }

    [Fact]
    public void RenameResponsePayload_roundtrip()
    {
        var ok = Roundtrip(new RenameResponsePayload { RequestId = "r1", Success = true });
        Assert.True(ok.Success);

        var fail = Roundtrip(new RenameResponsePayload { RequestId = "r2", Success = false, Message = "Access denied" });
        Assert.False(fail.Success);
        Assert.Equal("Access denied", fail.Message);
    }

    [Fact]
    public void RemoteFileItemPayload_with_date()
    {
        var p = Roundtrip(new RemoteFileItemPayload
        {
            Name = "file.txt", FullPath = @"C:\file.txt", IsDirectory = false,
            Size = 4096, DateModifiedMs = 1712764800000 // 2024-04-10
        });
        Assert.Equal(4096, p.Size);
        Assert.Equal(1712764800000, p.DateModifiedMs);
    }

    [Fact]
    public void DeleteRequestPayload_roundtrip()
    {
        var p = Roundtrip(new DeleteRequestPayload { Path = @"C:\temp\old", RequestId = "d1" });
        Assert.Equal(@"C:\temp\old", p.Path);
        Assert.Equal("d1", p.RequestId);
    }

    [Fact]
    public void CreateFolderRequestPayload_roundtrip()
    {
        var p = Roundtrip(new CreateFolderRequestPayload { ParentPath = @"C:\Users", FolderName = "New", RequestId = "cf1" });
        Assert.Equal(@"C:\Users", p.ParentPath);
        Assert.Equal("New", p.FolderName);
    }

    [Fact]
    public void HostVideoSettingsRequestPayload_all_fields()
    {
        var p = Roundtrip(new HostVideoSettingsRequestPayload
        {
            QualityPreset = "High", DisplayId = "DISPLAY2", QuickReconnect = true
        });
        Assert.Equal("High", p.QualityPreset);
        Assert.Equal("DISPLAY2", p.DisplayId);
        Assert.True(p.QuickReconnect);
    }

    [Fact]
    public void ScreenMetaPayload_roundtrip()
    {
        var p = Roundtrip(new ScreenMetaPayload { CaptureX = -1920, CaptureY = 0, Width = 1920, Height = 1080, DisplayId = "ALL" });
        Assert.Equal(-1920, p.CaptureX);
        Assert.Equal("ALL", p.DisplayId);
    }

    [Fact]
    public void CursorShapePayload_roundtrip()
    {
        var p = Roundtrip(new CursorShapePayload { CursorType = "ibeam" });
        Assert.Equal("ibeam", p.CursorType);
    }

    [Fact]
    public void ClipboardTextPayload_roundtrip()
    {
        var p = Roundtrip(new ClipboardTextPayload { Text = "Hello, clipboard!" });
        Assert.Equal("Hello, clipboard!", p.Text);
    }

    [Fact]
    public void MouseInputPayload_roundtrip()
    {
        var p = Roundtrip(new MouseInputPayload { X = 100, Y = 200, Button = 1, Action = "down" });
        Assert.Equal(100, p.X);
        Assert.Equal(200, p.Y);
        Assert.Equal(1, p.Button);
        Assert.Equal("down", p.Action);
    }

    [Fact]
    public void KeyboardInputPayload_roundtrip()
    {
        var p = Roundtrip(new KeyboardInputPayload { VirtualKey = 65, ScanCode = 30, Action = "down" });
        Assert.Equal(65, p.VirtualKey);
        Assert.Equal("down", p.Action);
    }
}
