namespace UiApp.Models;

/// <summary>
/// Папка на удалённой стороне, куда сохранять отправляемые файлы (→). Обновляется из правой панели File Transfer, когда правая панель показывает удалённый диск.
/// </summary>
public sealed class OutgoingTargetDirHolder
{
    public string? Path { get; set; }
}
