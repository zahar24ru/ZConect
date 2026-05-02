using FileTransfer;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for SafePath helper (path traversal prevention).</summary>
public sealed class SafePathTests
{
    [Theory]
    [InlineData(@"C:\Users\Downloads", @"C:\Users\Downloads\file.txt", true)]
    [InlineData(@"C:\Users\Downloads", @"C:\Users\Downloads\sub\file.txt", true)]
    [InlineData(@"C:\Users\Downloads", @"C:\Users\Documents\file.txt", false)]
    [InlineData(@"C:\Users\Downloads", @"C:\Windows\System32\cmd.exe", false)]
    [InlineData(@"C:\Users\Downloads\", @"C:\Users\Downloads\file.txt", true)]
    public void IsUnderRoot_validates_correctly(string root, string candidate, bool expected)
    {
        Assert.Equal(expected, SafePath.IsUnderRoot(root, candidate));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("subfolder/file.txt", false)]
    [InlineData(@"subfolder\file.txt", false)]
    [InlineData("../etc/passwd", true)]
    [InlineData(@"..\..\Windows\System32", true)]
    [InlineData(@"C:\absolute\path", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData("//server/share", true)]
    [InlineData("/etc/passwd", true)]
    [InlineData(@"topsecretkeys\Заявка на получение.pdf", false)] // subdirectory with Cyrillic — NOT dangerous
    [InlineData(@"folder\subfolder\file.txt", false)] // nested subdirectories — NOT dangerous
    [InlineData("filename..pdf", false)] // double dot in filename — NOT path traversal
    [InlineData(@"folder\file..txt", false)] // double dot in extension — NOT dangerous
    [InlineData(@"topsecretkeys\пункта..pdf", false)] // Cyrillic with double dot — NOT dangerous
    // Spaces in filenames
    [InlineData("  leading spaces.txt", false)]     // leading spaces — allowed (OS handles it)
    [InlineData("trailing spaces  .txt", false)]    // trailing spaces — allowed
    [InlineData("  both sides  .txt", false)]       // both sides — allowed
    [InlineData("normal file.txt", false)]          // space in middle — obviously OK
    // Dots in filenames
    [InlineData(".hidden", false)]                  // Unix-style hidden file — allowed
    [InlineData("..hidden", false)]                 // double dot at START of filename — NOT traversal (no separator after)
    [InlineData("file...txt", false)]               // triple dots — allowed
    [InlineData("file.", false)]                    // trailing dot — allowed
    [InlineData("..", true)]                        // JUST ".." — path traversal!
    [InlineData(@"..\..", true)]                    // double traversal
    [InlineData(@"..\file.txt", true)]              // traversal + file
    // Windows reserved device names
    [InlineData("COM1", true)]
    [InlineData("COM1.txt", true)]                  // COM1 with extension — still dangerous!
    [InlineData("NUL", true)]
    [InlineData("NUL.pdf", true)]
    [InlineData("PRN.doc", true)]
    [InlineData("AUX", true)]
    [InlineData("LPT1", true)]
    [InlineData("CON.txt", true)]
    [InlineData("com1", true)]                      // case insensitive
    [InlineData(@"folder\COM1.txt", true)]          // reserved name in subfolder
    [InlineData("COMMUNICATE.txt", false)]          // starts with COM but NOT a reserved name
    [InlineData("computer.pdf", false)]             // contains "com" but NOT reserved
    [InlineData("NULLable.cs", false)]              // starts with NUL but full name is different
    // Special characters — OS will reject, not our job
    [InlineData("file<name>.txt", false)]           // angle brackets
    [InlineData("file:name.txt", false)]            // colon in middle — not drive letter pattern
    [InlineData("file*name.txt", false)]            // asterisk
    [InlineData("file|name.txt", false)]            // pipe
    [InlineData("file\"name.txt", false)]           // quote
    public void IsDangerous_detects_traversal(string? path, bool expected)
    {
        Assert.Equal(expected, SafePath.IsDangerous(path));
    }

    [Fact]
    public void IsDangerous_rejects_very_long_paths()
    {
        var longPath = new string('a', 501);
        Assert.True(SafePath.IsDangerous(longPath));

        var okPath = new string('a', 200) + ".txt";
        Assert.False(SafePath.IsDangerous(okPath));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe", true)]
    [InlineData(@"C:\Program Files\app.exe", true)]
    [InlineData(@"C:\ProgramData\secret", true)]
    [InlineData(@"C:\Users\Downloads\file.txt", false)]
    [InlineData(@"D:\MyFolder\file.txt", false)]
    public void IsSystemPath_blocks_protected_dirs(string path, bool expected)
    {
        Assert.Equal(expected, SafePath.IsSystemPath(path));
    }
}
