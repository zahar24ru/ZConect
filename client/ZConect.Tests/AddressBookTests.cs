using UiApp.Models;
using UiApp.Services;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for AddressBookService: load, save, roundtrip, error handling.</summary>
public sealed class AddressBookTests : IDisposable
{
    private readonly string _tempDir;

    public AddressBookTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ZConect_AddressBookTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsEmptyList()
    {
        var path = Path.Combine(_tempDir, "does_not_exist.json");
        var svc = new AddressBookService(path);

        var result = svc.Load();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmptyList()
    {
        var path = Path.Combine(_tempDir, "corrupt.json");
        File.WriteAllText(path, "NOT VALID JSON {{{");
        var svc = new AddressBookService(path);

        var result = svc.Load();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void Save_ThenLoad_Roundtrip_ContactsMatch()
    {
        var path = Path.Combine(_tempDir, "contacts.json");
        var svc = new AddressBookService(path);

        var contacts = new List<Contact>
        {
            new Contact { Name = "Alice", LoginCode = "12345678", PassCode = "87654321", ServerApiBaseUrl = "https://example.com" },
            new Contact { Name = "Bob", LoginCode = "11111111", PassCode = "22222222", ServerApiBaseUrl = "http://192.168.1.10:8080" }
        };

        svc.Save(contacts);
        var loaded = svc.Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("Alice", loaded[0].Name);
        Assert.Equal("12345678", loaded[0].LoginCode);
        Assert.Equal("87654321", loaded[0].PassCode);
        Assert.Equal("https://example.com", loaded[0].ServerApiBaseUrl);
        Assert.Equal("Bob", loaded[1].Name);
        Assert.Equal("11111111", loaded[1].LoginCode);
    }

    [Fact]
    public void Save_CreatesDirectory_IfNotExists()
    {
        var nestedDir = Path.Combine(_tempDir, "sub", "nested");
        var path = Path.Combine(nestedDir, "contacts.json");
        // nestedDir does not exist yet
        Assert.False(Directory.Exists(nestedDir));

        var svc = new AddressBookService(path);
        // Save should create parent directory
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        svc.Save(new List<Contact>
        {
            new Contact { Name = "Test" }
        });

        Assert.True(File.Exists(path));
        var loaded = svc.Load();
        Assert.Single(loaded);
        Assert.Equal("Test", loaded[0].Name);
    }
}
