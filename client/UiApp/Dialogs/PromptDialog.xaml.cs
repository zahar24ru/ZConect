using System.Windows;
using System.Windows.Input;

namespace UiApp.Dialogs;

/// <summary>
/// Простой prompt для ввода строки (например, имя контакта). Два варианта выхода:
///  - Save (Enter / кнопка «Сохранить») → DialogResult=true, <see cref="Value"/>=введённый текст.
///  - Later (Esc / кнопка «Позже») → DialogResult=false, <see cref="Value"/>=null.
/// Используется в AutoSaveAdhocConnection для предложения переименовать свежий
/// «Сеанс XXXX» контакт в осмысленное имя.
/// </summary>
public sealed partial class PromptDialog : Window
{
    /// <summary>Введённое пользователем значение. Null если отменено.</summary>
    public string? Value { get; private set; }

    public PromptDialog(string title, string message, string initialValue)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(title)) TitleText.Text = title;
        if (!string.IsNullOrEmpty(message)) MessageText.Text = message;
        NameBox.Text = initialValue ?? string.Empty;

        var isDark = new Services.SettingsService().Load().ThemeMode == "Dark";
        MainWindow.ApplyDarkTitleBar(this, isDark);

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var v = NameBox.Text?.Trim() ?? string.Empty;
        if (v.Length == 0) return; // пустое имя не принимаем
        Value = v;
        DialogResult = true;
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        Value = null;
        DialogResult = false;
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveButton_Click(sender, e);
            e.Handled = true;
        }
    }
}
