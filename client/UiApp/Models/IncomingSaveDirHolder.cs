namespace UiApp.Models;

/// <summary>
/// Текущая папка для сохранения входящих файлов. Обновляется из правой панели File Transfer.
/// Если окно не открыто или путь не меняли — используется значение по умолчанию (ZConectReceived).
/// </summary>
public sealed class IncomingSaveDirHolder
{
    public string? Path { get; set; }
}
