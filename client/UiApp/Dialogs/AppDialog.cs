using System.Windows;

namespace UiApp.Dialogs;

/// <summary>Фабрика стилизованных диалогов приложения.</summary>
public static class AppDialog
{
    /// <summary>Показывает диалог подтверждения, центрированный по главному окну приложения.
    /// Если confirmText = null, используется локализованный Strings.Button_Delete по умолчанию.</summary>
    public static bool Confirm(string message, string? confirmText = null)
    {
        var owner = Application.Current?.MainWindow;
        var dlg = new ConfirmDialog(message, confirmText);
        if (owner is not null)
            dlg.Owner = owner;
        return dlg.ShowDialog() == true;
    }

    /// <summary>Prompt для ввода строки (имя контакта и т.п.). Возвращает введённое
    /// значение или null если пользователь нажал «Позже» / Esc.</summary>
    public static string? Prompt(string title, string message, string initialValue)
    {
        var owner = Application.Current?.MainWindow;
        var dlg = new PromptDialog(title, message, initialValue);
        if (owner is not null)
            dlg.Owner = owner;
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }
}
