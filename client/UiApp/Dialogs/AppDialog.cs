using System.Windows;

namespace UiApp.Dialogs;

/// <summary>Фабрика стилизованных диалогов приложения.</summary>
public static class AppDialog
{
    /// <summary>Показывает диалог подтверждения, центрированный по главному окну приложения.</summary>
    public static bool Confirm(string message, string confirmText = "Удалить")
    {
        var owner = Application.Current?.MainWindow;
        var dlg = new ConfirmDialog(message, confirmText);
        if (owner is not null)
            dlg.Owner = owner;
        return dlg.ShowDialog() == true;
    }
}
