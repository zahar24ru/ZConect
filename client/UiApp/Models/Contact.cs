using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UiApp.Models;

/// <summary>Контакт адресной книги для подключения без ввода кодов (режим «только передача файлов» или удалённый стол).</summary>
public sealed class Contact : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _serverApiBaseUrl = string.Empty;
    private string _loginCode = string.Empty;
    private string _passCode = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>Базовый URL API сервера (например https://example.com или http://192.168.1.10:8080).</summary>
    public string ServerApiBaseUrl
    {
        get => _serverApiBaseUrl;
        set { _serverApiBaseUrl = value; OnPropertyChanged(); }
    }

    public string LoginCode
    {
        get => _loginCode;
        set { _loginCode = value; OnPropertyChanged(); }
    }

    public string PassCode
    {
        get => _passCode;
        set { _passCode = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
