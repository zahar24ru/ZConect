using UiApp.Models;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for FileItem model (icons, colors, formatting).</summary>
public sealed class FileItemTests
{
    [Fact]
    public void Directory_shows_DIR()
    {
        var item = new FileItem { Name = "Documents", IsDirectory = true };
        Assert.Equal("<DIR>", item.SizeText);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824L, "1 GB")]
    public void SizeText_formatting(long size, string expected)
    {
        var item = new FileItem { Name = "f.bin", Size = size };
        Assert.Equal(expected, item.SizeText);
    }

    [Fact]
    public void DateText_default_is_empty()
    {
        var item = new FileItem { Name = "f.txt" };
        Assert.Equal("", item.DateText);
    }

    [Fact]
    public void DateText_format()
    {
        var item = new FileItem { Name = "f.txt", DateModified = new DateTime(2026, 4, 10, 14, 30, 0) };
        Assert.Equal("10.04.26 14:30", item.DateText);
    }

    [Fact]
    public void Folder_icon_is_orange()
    {
        var item = new FileItem { Name = "Docs", IsDirectory = true };
        Assert.Equal("#F0A030", item.IconColor);
        Assert.NotEmpty(item.IconGeometry);
    }

    [Theory]
    [InlineData(".jpg", "#4CAF50")]
    [InlineData(".png", "#4CAF50")]
    [InlineData(".gif", "#4CAF50")]
    [InlineData(".cs", "#42A5F5")]
    [InlineData(".js", "#42A5F5")]
    [InlineData(".json", "#42A5F5")]
    [InlineData(".zip", "#AB47BC")]
    [InlineData(".rar", "#AB47BC")]
    [InlineData(".mp3", "#FF7043")]
    [InlineData(".mp4", "#EF5350")]
    [InlineData(".exe", "#78909C")]
    [InlineData(".iso", "#8D6E63")]
    [InlineData(".xyz", "#90A4AE")] // unknown
    public void IconColor_by_extension(string ext, string expectedColor)
    {
        var item = new FileItem { Name = "file" + ext };
        Assert.Equal(expectedColor, item.IconColor);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".cs")]
    [InlineData(".zip")]
    [InlineData(".mp3")]
    [InlineData(".mp4")]
    [InlineData(".exe")]
    [InlineData(".iso")]
    [InlineData(".txt")]
    public void IconGeometry_not_empty(string ext)
    {
        var item = new FileItem { Name = "file" + ext };
        Assert.NotEmpty(item.IconGeometry);
    }

    [Fact]
    public void File_and_folder_have_different_icons()
    {
        var file = new FileItem { Name = "readme.txt" };
        var folder = new FileItem { Name = "Documents", IsDirectory = true };
        Assert.NotEqual(file.IconGeometry, folder.IconGeometry);
    }
}
