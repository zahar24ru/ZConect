using Microsoft.Win32;
using System.Windows;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>
    /// Original PromptOnSecureDesktop value from HKLM, snapshotted перед disable.
    /// null = мы НЕ меняли policy, restore не нужен.
    /// 1 = было enabled (default Windows) — при restore вернём 1.
    /// 0 = было уже disabled пользователем намеренно — при restore оставим 0
    ///     (не будем менять его setting без permission).
    /// </summary>
    private int? _savedSecureDesktopValue;

    /// <summary>
    /// Disable Secure Desktop while viewer is connected. После Stage 1 UI
    /// работает под user token и НЕ МОЖЕТ писать в HKLM\...\Policies\System
    /// (нужен admin/SYSTEM). Делегируем в service через pipe — service в
    /// session 0 под SYSTEM имеет права. Результат: UAC показывается на
    /// Default desktop (UI capture видит его, direct injection на Default
    /// кликает как обычно).
    ///
    /// F-11 (external audit 2026-04-18): snapshot реальное HKLM значение
    /// перед disable, чтобы restore вернул exactly то что было. Раньше было
    /// hardcoded = 1 → при crash между disable и restore UAC'а user'а
    /// оставалась permanently disabled; плюс если original был 0
    /// (user намеренно), restore возвращал 1 → silent override user setting.
    /// </summary>
    private void DisableSecureDesktopForRemote()
    {
        _savedSecureDesktopValue = ReadSecureDesktopPolicyValue();
        if (_savedSecureDesktopValue == 0)
        {
            // Already disabled — no change needed, no restore needed.
            _logService.Info("UiApp", "uac_secure_desktop_already_disabled_no_action");
            _savedSecureDesktopValue = null; // mark as "не меняли, restore skip"
            return;
        }
        _ = _servicePipe?.SetUacSecureDesktopAsync(disable: true);
        _logService.Info("UiApp", $"uac_secure_desktop_disable_requested_via_service saved_original={_savedSecureDesktopValue}");
    }

    /// <summary>Restore Secure Desktop when viewer disconnects (via service pipe).</summary>
    private void RestoreSecureDesktopPolicy()
    {
        if (_savedSecureDesktopValue is null) return;
        try
        {
            // disable:false → service ставит PromptOnSecureDesktop=1. Это единственное
            // поведение service API на данный момент. Если original был 1 (default) —
            // это правильно. Если бы original был 0 — мы бы не оказались здесь
            // (DisableSecureDesktopForRemote early return + _savedSecureDesktopValue=null).
            _ = _servicePipe?.SetUacSecureDesktopAsync(disable: false);
            _logService.Info("UiApp", $"uac_secure_desktop_restore_requested_via_service target_value={_savedSecureDesktopValue}");
        }
        finally
        {
            _savedSecureDesktopValue = null;
            RefreshUacPolicyState();
        }
    }

    /// <summary>
    /// Reads current PromptOnSecureDesktop value from HKLM. Returns 1 if missing
    /// (Windows default: UAC enabled). Returns null on read failure.
    /// F-11 support: used for snapshotting original before change.
    /// </summary>
    private int? ReadSecureDesktopPolicyValue()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UacPolicyRegistryPath, writable: false);
            var raw = key?.GetValue(UacPromptOnSecureDesktopValueName);
            return raw switch
            {
                int i => i,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => 1  // default Windows behaviour when key missing = UAC enabled
            };
        }
        catch (Exception ex)
        {
            _logService.Warn("UiApp", $"uac_policy_snapshot_failed: {ex.Message}");
            return null;
        }
    }

    private void RefreshUacPolicyState()
    {
        try
        {
            // HKLM read-only open работает под user token (не нужны admin права).
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
        if (result != MessageBoxResult.Yes) return;

        // Под user token прямая запись в HKLM\...\Policies\System не работает.
        // Service под SYSTEM имеет права — делегируем через pipe.
        if (_servicePipe is null || !_servicePipe.IsConnected)
        {
            StatusText = "Не удалось изменить UAC политику: сервис не подключён";
            OnPropertyChanged(nameof(StatusText));
            _logService.Warn("UiApp", "uac_toggle_failed_service_pipe_not_connected");
            return;
        }

        try
        {
            await _servicePipe.SetUacSecureDesktopAsync(disable: disableSecureDesktop);
            // Service пишет в HKLM асинхронно; дадим ~300ms на propagate прежде чем читать назад.
            await Task.Delay(300);
            RefreshUacPolicyState();

            StatusText = disableSecureDesktop
                ? "UAC: показ в удаленной сессии разрешен"
                : "UAC: защищенный рабочий стол включен";
            OnPropertyChanged(nameof(StatusText));
            _logService.Info("UiApp", disableSecureDesktop ? "uac_secure_desktop_disabled_via_service" : "uac_secure_desktop_enabled_via_service");
        }
        catch (Exception ex)
        {
            StatusText = "Не удалось изменить UAC политику";
            OnPropertyChanged(nameof(StatusText));
            _logService.Error("UiApp", "uac_policy_toggle_failed", ex.Message);
        }
    }
}
