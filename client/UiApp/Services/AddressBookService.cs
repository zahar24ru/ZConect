using System.IO;
using System.Text.Json;
using UiApp.Models;

namespace UiApp.Services;

/// <summary>Хранит адресную книгу контактов в %AppData%\ZConect\address-book.json. Данные чувствительны — хранятся локально.</summary>
public sealed class AddressBookService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string AddressBookPath { get; }

    public AddressBookService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZConect");
        Directory.CreateDirectory(dir);
        AddressBookPath = Path.Combine(dir, "address-book.json");
    }

    public List<Contact> Load()
    {
        if (!File.Exists(AddressBookPath))
        {
            return new List<Contact>();
        }

        try
        {
            var raw = File.ReadAllText(AddressBookPath);
            var list = JsonSerializer.Deserialize<List<Contact>>(raw, JsonOptions);
            return list ?? new List<Contact>();
        }
        catch
        {
            return new List<Contact>();
        }
    }

    public void Save(List<Contact> contacts)
    {
        var raw = JsonSerializer.Serialize(contacts, JsonOptions);
        File.WriteAllText(AddressBookPath, raw);
    }
}
