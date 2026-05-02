using System.Windows;
using UiApp.Properties;

namespace UiApp.Dialogs;

/// <summary>
/// Host-side dialog для задания / смены unattended password'а.
/// Validates: оба поля match, min length 6 chars.
/// </summary>
public sealed partial class UnattendedPasswordSetupDialog : Window
{
    private const int MinPasswordLength = 6;

    public string Password { get; private set; } = string.Empty;

    public UnattendedPasswordSetupDialog()
    {
        InitializeComponent();
        var isDark = new Services.SettingsService().Load().ThemeMode == "Dark";
        MainWindow.ApplyDarkTitleBar(this, isDark);
        Loaded += (_, _) => Pwd1.Focus();
        // Scrim stuck workaround (см. UnattendedPasswordDialog).
        Closed += (_, _) =>
        {
            if (System.Windows.Application.Current?.MainWindow is MainWindow mw)
                mw.ForceScrimRefresh();
        };
    }

    private string Pwd1Value =>
        Pwd1.Visibility == Visibility.Visible ? Pwd1.Password ?? string.Empty : Pwd1Visible.Text ?? string.Empty;
    private string Pwd2Value =>
        Pwd2.Visibility == Visibility.Visible ? Pwd2.Password ?? string.Empty : Pwd2Visible.Text ?? string.Empty;

    private void Pwd_Changed(object sender, RoutedEventArgs e)
    {
        var a = Pwd1Value;
        var b = Pwd2Value;

        // Strength indicator — обновляется сразу при вводе (ещё до валидации match).
        UpdateStrengthIndicator(a);

        if (a.Length < MinPasswordLength)
        {
            StatusText.Text = string.Format(Strings.Password_MinLength_Format, MinPasswordLength, MinPasswordLength - a.Length);
            SaveButton.IsEnabled = false;
            return;
        }

        if (a != b)
        {
            StatusText.Text = Strings.Password_Mismatch;
            SaveButton.IsEnabled = false;
            return;
        }

        StatusText.Text = Strings.Password_Valid;
        SaveButton.IsEnabled = true;
    }

    /// <summary>Простая оценка силы password'а: длина + character class diversity. Показывает
    /// filled bar (0..100% ширины контейнера через Grid Star-units, без хардкода pixel'ов) +
    /// текст «Слабый/Средний/Хороший/Сильный».</summary>
    private void UpdateStrengthIndicator(string pwd)
    {
        var score = ComputeStrengthScore(pwd);  // 0..100
        // Star-units: fill column = score*, empty column = (100-score)*. Layout engine сам
        // распределяет ширину пропорционально — при score=100 fill занимает всю полосу.
        StrengthFillColumn.Width = new System.Windows.GridLength(score, System.Windows.GridUnitType.Star);
        StrengthEmptyColumn.Width = new System.Windows.GridLength(System.Math.Max(100 - score, 0), System.Windows.GridUnitType.Star);

        string label;
        string color;
        if (string.IsNullOrEmpty(pwd))        { label = "";                            color = "#9CA3AF"; }
        else if (score < 30)                  { label = Strings.Password_Strength_Weak;   color = "#EF4444"; }
        else if (score < 55)                  { label = Strings.Password_Strength_Medium; color = "#F59E0B"; }
        else if (score < 80)                  { label = Strings.Password_Strength_Good;   color = "#22C55E"; }
        else                                  { label = Strings.Password_Strength_Strong; color = "#10B981"; }

        StrengthLabel.Text = label;
        var brush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)!);
        StrengthFill.Background = brush;
        StrengthLabel.Foreground = brush;
    }

    private static int ComputeStrengthScore(string pwd)
    {
        if (string.IsNullOrEmpty(pwd)) return 0;
        var len = pwd.Length;
        var hasLower = false; var hasUpper = false; var hasDigit = false; var hasSymbol = false;
        foreach (var c in pwd)
        {
            if (char.IsLower(c)) hasLower = true;
            else if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else hasSymbol = true;
        }
        // Base: длина (clamped 0..50)
        var score = Math.Min(len * 4, 50);
        // Diversity: каждый class добавляет бонус
        if (hasLower) score += 10;
        if (hasUpper) score += 12;
        if (hasDigit) score += 12;
        if (hasSymbol) score += 16;
        return Math.Min(score, 100);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var a = Pwd1Value;
        var b = Pwd2Value;
        if (a.Length < MinPasswordLength || a != b) return;
        Password = a;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>Toggle plain-text visibility для обоих password fields одновременно.</summary>
    private void ToggleShow_Click(object sender, RoutedEventArgs e)
    {
        if (Pwd1.Visibility == Visibility.Visible)
        {
            // Hide → Show
            Pwd1Visible.Text = Pwd1.Password;
            Pwd2Visible.Text = Pwd2.Password;
            Pwd1.Visibility = Visibility.Collapsed;
            Pwd2.Visibility = Visibility.Collapsed;
            Pwd1Visible.Visibility = Visibility.Visible;
            Pwd2Visible.Visibility = Visibility.Visible;
            ToggleShowButton.Content = Strings.Dialog_SetPassword_HideButton;
        }
        else
        {
            // Show → Hide
            Pwd1.Password = Pwd1Visible.Text;
            Pwd2.Password = Pwd2Visible.Text;
            Pwd1Visible.Visibility = Visibility.Collapsed;
            Pwd2Visible.Visibility = Visibility.Collapsed;
            Pwd1.Visibility = Visibility.Visible;
            Pwd2.Visibility = Visibility.Visible;
            ToggleShowButton.Content = Strings.Dialog_SetPassword_ShowButton;
        }
    }
}
