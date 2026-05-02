using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using UiApp.Dialogs;
using UiApp.Properties;
using UiApp.ViewModels;

namespace UiApp;

public partial class SystemSettingsWindow : Window
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public SystemSettingsWindow()
    {
        InitializeComponent();
        var svc = new Services.SettingsService();
        var settings = svc.Load();
        MainWindow.RestoreWindowRect(this, settings.SettingsWindowRect);
        Closing += OnClosing;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
        DataContextChanged += (_, _) => RefreshUnattendedPasswordUi();
        Loaded += (_, _) =>
        {
            RefreshUnattendedPasswordUi();
            // TURN status panel — start 1s timer (Variant C UI 2026-04-25). Live countdown
            // expires_in отображается через DispatcherTimer на ViewModel'е. Stop при Closing.
            Vm?.StartTurnStatusTimer();
            Vm?.RefreshTurnStatusBindings(); // initial values
        };
        Closed += (_, _) => Vm?.StopTurnStatusTimer();

        // Populate language dropdown
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Русский", Tag = "ru-RU" });
        LanguageCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "English", Tag = "en-US" });
        // Select current
        var currentCode = settings.UiLanguage;
        if (string.IsNullOrEmpty(currentCode)) currentCode = "ru-RU";
        foreach (System.Windows.Controls.ComboBoxItem item in LanguageCombo.Items)
        {
            if ((string?)item.Tag == currentCode) { LanguageCombo.SelectedItem = item; break; }
        }

        // Populate dynamic About-tab version text from assembly metadata.
        var asm = typeof(SystemSettingsWindow).Assembly;
        var ver = asm.GetName().Version;
        AboutVersionText.Text = ver is not null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v?";
        try
        {
            var file = asm.Location;
            if (!string.IsNullOrEmpty(file) && System.IO.File.Exists(file))
            {
                var buildDate = System.IO.File.GetLastWriteTime(file);
                AboutBuildText.Text = string.Format(Strings.About_Build_Format, buildDate.ToString("yyyy-MM-dd HH:mm"));
            }
        }
        catch { /* best effort */ }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Vm?.SaveSettings();
        try
        {
            var svc = new Services.SettingsService();
            var s = svc.Load();
            s.SettingsWindowRect = MainWindow.GetWindowRect(this);
            svc.Save(s);
        }
        catch { /* best effort */ }
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        Vm?.OpenLogFile();
    }

    private void ClearLogFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmDialog(Strings.Settings_ClearLogs_Confirm) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Vm?.ClearLogFiles();
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // ── Unattended password management ───────────────────────────

    private void RefreshUnattendedPasswordUi()
    {
        var isSet = Vm?.IsUnattendedPasswordSet ?? false;
        var hasLockout = Vm?.HasUnattendedLockout ?? false;
        UnattendedPasswordStatus.Text = isSet
            ? Strings.Settings_Unattended_Status_Set
            : Strings.Settings_Unattended_Status_NotRequired;
        SetUnattendedPasswordButton.Content = isSet ? Strings.Settings_ChangePassword : Strings.Settings_SetPassword;
        ClearUnattendedPasswordButton.Visibility = isSet ? Visibility.Visible : Visibility.Collapsed;
        // Lockout warning panel — красный текст + кнопка на отдельной строке;
        // видна только когда активен counter/tier/until. Подробный текст (сколько осталось)
        // строится из VM LockoutDescription.
        LockoutWarningPanel.Visibility = hasLockout ? Visibility.Visible : Visibility.Collapsed;
        if (hasLockout && Vm is not null)
        {
            LockoutWarningText.Text = Vm.UnattendedLockoutDescription;
        }
    }

    private void SetUnattendedPassword_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dlg = new UnattendedPasswordSetupDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;

        try
        {
            Vm.SetUnattendedPassword(dlg.Password);
            RefreshUnattendedPasswordUi();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, Strings.Error_Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearUnattendedPassword_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var confirm = new ConfirmDialog(Strings.Settings_ClearUnattendedPassword_Confirm, Strings.Button_Delete) { Owner = this };
        if (confirm.ShowDialog() != true) return;
        Vm.ClearUnattendedPassword();
        RefreshUnattendedPasswordUi();
    }

    private void ResetLockout_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.ResetUnattendedLockout();
        RefreshUnattendedPasswordUi();
    }

    // ── Service tab handlers ─────────────────────────────────────

    private void ServiceInstall_Click(object sender, RoutedEventArgs e) => Vm?.InstallService();
    private void ServiceUninstall_Click(object sender, RoutedEventArgs e) => Vm?.UninstallService();
    private void ServiceStart_Click(object sender, RoutedEventArgs e) => Vm?.RunServiceCommand("start");
    private void ServiceStop_Click(object sender, RoutedEventArgs e) => Vm?.RunServiceCommand("stop");
    private void ServiceRefresh_Click(object sender, RoutedEventArgs e) => Vm?.RefreshServiceStatus();
    private bool _langInit = false;
    private void LanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Skip initial load — save only on user change
        if (!_langInit) { _langInit = true; return; }
        if (LanguageCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        var code = (string?)item.Tag ?? "";
        // Save via ViewModel so _settings синхронно обновляется — OnClosing
        // потом делает Vm.SaveSettings() и без этого UiLanguage был бы
        // перезаписан stale "". Если Vm нет (не должно быть), fallback на прямое
        // сохранение через SettingsService.
        if (Vm is not null)
        {
            Vm.UiLanguage = code;
            Vm.SaveSettings();
        }
        else
        {
            var svc = new Services.SettingsService();
            var s = svc.Load();
            s.UiLanguage = code;
            svc.Save(s);
        }
        // Show inline hint чтобы user знал что нужен restart.
        // Message сам локализуется: после Save() выше Culture всё ещё старая
        // (применяется на restart), поэтому берём под ТЕКУЩИЙ выбранный язык
        // через ResourceManager с явным culture — user видит сообщение уже на
        // выбранном языке.
        var ci = new System.Globalization.CultureInfo(string.IsNullOrEmpty(code) ? "ru-RU" : code);
        var msg = (code == "en-US")
            ? "Language saved. Restart the app to apply."
            : "Язык сохранён. Перезапустите приложение чтобы применить.";
        MessageBox.Show(this, msg, "ZConnect", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ServiceOpenConfig_Click(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZConect");
        if (System.IO.Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    /// <summary>Manual update check — создаёт one-shot UpdateChecker, делает check
    /// и показывает результат прямо под кнопкой. Не запускает periodic timer (это
    /// делает MainWindow на startup).</summary>
    private async void CheckUpdateNow_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        if (btn is not null) btn.IsEnabled = false;
        CheckUpdateResult.Text = Strings.Status_Checking;
        CheckUpdateResult.Foreground = (System.Windows.Media.Brush?)Application.Current?.Resources["TextSecondaryBrush"]
                                       ?? System.Windows.Media.Brushes.Gray;

        try
        {
            var svc = new Services.SettingsService();
            var settings = svc.Load();
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var url = settings.ServerApiBaseUrl.TrimEnd('/') + "/api/v1/client/version";
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                CheckUpdateResult.Text = string.Format(Strings.UpdateCheck_ServerReturned_Format, (int)resp.StatusCode);
                CheckUpdateResult.Foreground = (System.Windows.Media.Brush?)Application.Current?.Resources["DangerBrush"]
                                               ?? System.Windows.Media.Brushes.Red;
                return;
            }
            var json = await resp.Content.ReadAsStringAsync();
            var info = System.Text.Json.JsonSerializer.Deserialize<Services.UpdateInfo>(json);
            if (info is null || string.IsNullOrWhiteSpace(info.LatestVersion))
            {
                CheckUpdateResult.Text = Strings.UpdateCheck_EmptyResponse;
                CheckUpdateResult.Foreground = (System.Windows.Media.Brush?)Application.Current?.Resources["DangerBrush"]
                                               ?? System.Windows.Media.Brushes.Red;
                return;
            }
            var current = typeof(SystemSettingsWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
            var currentShort = new Version(current.Major, current.Minor, current.Build);
            if (Version.TryParse(info.LatestVersion, out var latest))
            {
                var latestShort = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
                if (latestShort > currentShort)
                {
                    CheckUpdateResult.Text = string.Format(Strings.UpdateCheck_Available_Format, info.LatestVersion, currentShort);
                    CheckUpdateResult.Foreground = (System.Windows.Media.Brush?)Application.Current?.Resources["PrimaryBrush"]
                                                   ?? System.Windows.Media.Brushes.Green;
                    // Поднимаем badge на MainWindow:
                    if (Vm is not null)
                    {
                        Vm.LatestVersionText = info.LatestVersion;
                        Vm.UpdateDownloadUrl = info.DownloadUrl;
                        Vm.UpdateReleaseNotes = info.ReleaseNotes;
                        Vm.UpdateSha256 = info.Sha256;
                        Vm.IsUpdateAvailable = true;
                    }
                }
                else
                {
                    CheckUpdateResult.Text = string.Format(Strings.UpdateCheck_UpToDate_Format, currentShort);
                    CheckUpdateResult.Foreground = (System.Windows.Media.Brush?)Application.Current?.Resources["TextSecondaryBrush"]
                                                   ?? System.Windows.Media.Brushes.Gray;
                }
            }
            else
            {
                CheckUpdateResult.Text = string.Format(Strings.UpdateCheck_InvalidVersion_Format, info.LatestVersion);
                CheckUpdateResult.Foreground = (System.Windows.Media.Brush?)Application.Current?.Resources["DangerBrush"]
                                               ?? System.Windows.Media.Brushes.Red;
            }
        }
        catch (Exception ex)
        {
            CheckUpdateResult.Text = string.Format(Strings.UpdateCheck_Error_Format, ex.Message);
            CheckUpdateResult.Foreground = (System.Windows.Media.Brush?)Application.Current?.Resources["DangerBrush"]
                                           ?? System.Windows.Media.Brushes.Red;
        }
        finally
        {
            if (btn is not null) btn.IsEnabled = true;
        }
    }
}
