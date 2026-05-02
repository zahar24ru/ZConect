using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UiApp.Properties;

namespace UiApp.Dialogs;

/// <summary>
/// Viewer-side password prompt для unattended auth. Возвращает:
///   DialogResult=true + Password не пустой → user ввёл password, shлём proof host'у.
///   DialogResult=false → user выбрал «Запросить разрешение» (или Esc) → host покажет confirmation.
/// Parameters configure UX на retry (wrong password count remaining).
/// </summary>
public sealed partial class UnattendedPasswordDialog : Window
{
    private const int MinPasswordLength = 6;

    public string Password { get; private set; } = string.Empty;
    /// <summary>True если user отметил «Запомнить пароль». Valid только при DialogResult=true.</summary>
    public bool RememberPassword { get; private set; }

    private DispatcherTimer? _lockoutTimer;
    private int _lockoutSecondsRemaining;

    public UnattendedPasswordDialog(
        bool isRetry = false,
        int attemptsLeft = 0,
        string? lastReason = null,
        bool isLockedOut = false,
        int lockoutRemainingSec = 0,
        int lockoutTier = 0,
        bool showRememberCheckbox = false,
        string? contactName = null)
    {
        InitializeComponent();

        if (showRememberCheckbox && !isLockedOut)
        {
            RememberPasswordCheckBox.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(contactName))
                RememberPasswordCheckBox.Content = string.Format(Strings.UnattendedPassword_RememberForContact_Format, contactName);
        }

        if (isLockedOut)
        {
            EnterLockoutMode(lockoutRemainingSec, lockoutTier);
        }
        else if (isRetry)
        {
            RetryBanner.Visibility = Visibility.Visible;
            if (attemptsLeft > 0)
            {
                RetryText.Text = attemptsLeft == 1
                    ? Strings.UnattendedPassword_LastAttempt
                    : string.Format(Strings.UnattendedPassword_IncorrectWithAttempts_Format, attemptsLeft);
            }
            else
            {
                RetryText.Text = Strings.UnattendedPassword_Incorrect;
            }
        }

        var isDark = new Services.SettingsService().Load().ThemeMode == "Dark";
        MainWindow.ApplyDarkTitleBar(this, isDark);
        Loaded += (_, _) => { if (!isLockedOut) PasswordBox.Focus(); };
        // Scrim stuck workaround — MainWindow.Activated не fire'ится если focus уходит
        // мимо неё после close. Принудительно обновляем scrim состояние через public helper.
        Closed += (_, _) =>
        {
            _lockoutTimer?.Stop();
            if (System.Windows.Application.Current?.MainWindow is MainWindow mw)
                mw.ForceScrimRefresh();
        };
    }

    /// <summary>Lockout mode: disable password input + Connect button, показать countdown timer.</summary>
    private void EnterLockoutMode(int remainingSec, int tier)
    {
        _lockoutSecondsRemaining = remainingSec > 0 ? remainingSec : 60;

        RetryBanner.Visibility = Visibility.Visible;
        UpdateLockoutText(tier);

        // Disable password input + Connect button, оставить только «Закрыть».
        PasswordBox.IsEnabled = false;
        PasswordTextBox.IsEnabled = false;
        TogglePasswordVisibilityButton.IsEnabled = false;
        ConnectButton.Visibility = Visibility.Collapsed;
        WaitButton.Content = Strings.Button_Close;
        LengthHint.Visibility = Visibility.Collapsed;

        // Timer обновляет countdown каждую секунду.
        _lockoutTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = System.TimeSpan.FromSeconds(1),
        };
        _lockoutTimer.Tick += (_, _) =>
        {
            _lockoutSecondsRemaining--;
            if (_lockoutSecondsRemaining <= 0)
            {
                _lockoutTimer?.Stop();
                RetryText.Text = Strings.UnattendedPassword_LockoutLifted;
                return;
            }
            UpdateLockoutText(tier);
        };
        _lockoutTimer.Start();
    }

    private void UpdateLockoutText(int tier)
    {
        var min = _lockoutSecondsRemaining / 60;
        var sec = _lockoutSecondsRemaining % 60;
        var tierHint = tier >= 2
            ? string.Format(Strings.UnattendedPassword_TierHint_Format, tier)
            : string.Empty;
        var countdown = $"{min:D1}:{sec:D2}{tierHint}";
        RetryText.Text = string.Format(Strings.UnattendedPassword_LockoutCountdown_Format, countdown);
    }

    /// <summary>Текущее значение password'а независимо от какого поля сейчас visible.</summary>
    private string GetPassword() =>
        PasswordBox.Visibility == Visibility.Visible
            ? PasswordBox.Password ?? string.Empty
            : PasswordTextBox.Text ?? string.Empty;

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) => UpdateLengthHint();
    private void PasswordTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateLengthHint();

    private void UpdateLengthHint()
    {
        var len = GetPassword().Length;
        ConnectButton.IsEnabled = len >= MinPasswordLength;
        LengthHint.Text = len < MinPasswordLength
            ? string.Format(Strings.Password_MinLength_Format, MinPasswordLength, MinPasswordLength - len)
            : Strings.UnattendedPassword_LengthOk;
    }

    private void TogglePasswordVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Visibility == Visibility.Visible)
        {
            // Hide → Show
            PasswordTextBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordTextBox.Visibility = Visibility.Visible;
            PasswordTextBox.CaretIndex = PasswordTextBox.Text.Length;
            PasswordTextBox.Focus();
            TogglePasswordVisibilityButton.Content = "🙈";
        }
        else
        {
            // Show → Hide
            PasswordBox.Password = PasswordTextBox.Text;
            PasswordTextBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            PasswordBox.Focus();
            TogglePasswordVisibilityButton.Content = "👁";
        }
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ConnectButton.IsEnabled)
        {
            ConnectButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var pwd = GetPassword();
        if (pwd.Length < MinPasswordLength) return;
        Password = pwd;
        RememberPassword = RememberPasswordCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private void WaitButton_Click(object sender, RoutedEventArgs e)
    {
        Password = string.Empty;
        DialogResult = false;
    }
}
