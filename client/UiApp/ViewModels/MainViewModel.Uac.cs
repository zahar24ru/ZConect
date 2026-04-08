using Microsoft.Win32;
using System.Windows;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel
{
    private void RefreshUacPolicyState()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UacPolicyRegistryPath, writable: false);
            var raw = key?.GetValue(UacPromptOnSecureDesktopValueName);
            var value = raw switch
            {
                int i    => i,
                string s when int.TryParse(s, out var parsed) => parsed,
                _        => 1
            };
            UacPromptOnSecureDesktopEnabled = value != 0;
        }
        catch (Exception ex)
        {
            UacPromptOnSecureDesktopEnabled = true;
            _logService.Warn("UiApp", "uac_policy_read_failed_" + ex.Message);
        }
    }

    private async Task ToggleUacSecureDesktopPolicyAsync()
    {
        var disableSecureDesktop = UacPromptOnSecureDesktopEnabled;
        var warning = disableSecureDesktop
            ? "Отключить 'защищенный рабочий стол' для UAC?\n\nЭто позволит видеть запросы UAC в удаленной сессии, но снижает безопасность локального ПК."
            : "Включить обратно 'защищенный рабочий стол' для UAC?\n\nЭто повысит безопасность, но запросы UAC могут перестать отображаться в удаленной сессии.";
        var result = MessageBox.Show(warning, "UAC политика", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UacPolicyRegistryPath, writable: true);
            if (key is null)
            {
                StatusText = "Не удалось открыть UAC политику (реестр)";
                OnPropertyChanged(nameof(StatusText));
                _logService.Warn("UiApp", "uac_policy_open_failed");
                return;
            }

            key.SetValue(UacPromptOnSecureDesktopValueName, disableSecureDesktop ? 0 : 1, RegistryValueKind.DWord);
            RefreshUacPolicyState();

            StatusText = disableSecureDesktop
                ? "UAC: показ в удаленной сессии разрешен"
                : "UAC: защищенный рабочий стол включен";
            OnPropertyChanged(nameof(StatusText));
            _logService.Info("UiApp", disableSecureDesktop ? "uac_secure_desktop_disabled" : "uac_secure_desktop_enabled");
        }
        catch (Exception ex)
        {
            StatusText = "Не удалось изменить UAC политику";
            OnPropertyChanged(nameof(StatusText));
            _logService.Error("UiApp", "uac_policy_write_failed", ex.Message);
        }

        await Task.CompletedTask;
    }
}
