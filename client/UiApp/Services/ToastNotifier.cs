using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace UiApp.Services;

/// <summary>
/// Windows 10/11 native toast notifications — в правом нижнем углу экрана +
/// в Action Center.
///
/// ShowBalloonTip у NotifyIcon — legacy WinForms API, Win11 часто подавляет через
/// Focus Assist / notification policy. ToastNotification (WinRT) — modern API,
/// работает надёжно, respect'ит user'ские notification settings.
///
/// Важно: WinRT toast требует **AUMID registered**. Нужны ДВЕ вещи:
///  1. SetCurrentProcessExplicitAppUserModelID — процесс ассоциирован с AUMID.
///  2. AUMID внесён в registry HKCU\Software\Classes\AppUserModelId\{aumid} —
///     Windows знает DisplayName/IconUri для этого AUMID.
/// Без #2 CreateToastNotifier падает с COMException 0x803E0110.
///
/// Register() делает оба шага + опционально copy'ит icon to LocalAppData чтобы
/// задать IconUri. На Win10 также Falls back на NotifyIcon.ShowBalloonTip если
/// toast не работает (например приложение запущено с не-user правами).
/// </summary>
public static class ToastNotifier
{
    private const string DefaultAumid = "ZConnect";
    private static string _aumid = DefaultAumid;
    private static bool _registered;
    private static Action<string>? _log;

    /// <summary>Fallback callback — вызывается если WinRT toast падает. Вызовёт
    /// ShowBalloonTip через NotifyIcon (если tray icon показан). Устанавливается
    /// из MainWindow.InitNotifyIcon.</summary>
    public static Action<string, string>? BalloonTipFallback { get; set; }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>Регистрирует AppUserModelID в процессе + в HKCU registry чтобы
    /// WinRT ToastNotificationManager.CreateToastNotifier принял AUMID. Вызывается
    /// из App.OnStartup. onLog опционально для debug'а (appendится в LogService).</summary>
    public static void Register(string aumid = DefaultAumid, Action<string>? onLog = null)
    {
        _log = onLog;
        if (_registered) return;

        try { SetCurrentProcessExplicitAppUserModelID(aumid); _aumid = aumid; }
        catch (Exception ex) { _log?.Invoke($"toast_aumid_set_failed: {ex.Message}"); }

        // HKCU\Software\Classes\AppUserModelId\{aumid}
        // Mandatory для CreateToastNotifier на Win10/11 когда приложение НЕ из Store.
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{aumid}", true);
            if (key is not null)
            {
                key.SetValue("DisplayName", "ZConnect", RegistryValueKind.String);
                // IconUri — опционально, если заданий icon file, toast с ней отобразится.
                // Пропустим для простоты — default icon от ZConnect (system) покажется.
                _log?.Invoke($"toast_aumid_registered aumid={aumid}");
            }
        }
        catch (Exception ex) { _log?.Invoke($"toast_registry_write_failed: {ex.Message}"); }

        _registered = true;
    }

    /// <summary>Показать системный toast в правом нижнем углу. Best-effort — если
    /// user отключил уведомления / Focus Assist активен, toast подавляется (это
    /// ожидаемое поведение Win10/11, не bug). Если CreateToastNotifier падает —
    /// fallback на NotifyIcon.ShowBalloonTip через BalloonTipFallback.</summary>
    public static void Show(string title, string body)
    {
        var triedToast = false;
        try
        {
            triedToast = true;
            var titleEsc = System.Security.SecurityElement.Escape(title) ?? "";
            var bodyEsc = System.Security.SecurityElement.Escape(body) ?? "";
            var xml = "<toast duration='short'><visual><binding template='ToastGeneric'>"
                + $"<text>{titleEsc}</text>"
                + $"<text>{bodyEsc}</text>"
                + "</binding></visual></toast>";

            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var toast = new ToastNotification(doc);
            ToastNotificationManager.CreateToastNotifier(_aumid).Show(toast);
            _log?.Invoke($"toast_shown title=\"{title}\" aumid={_aumid}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"toast_show_failed: {ex.GetType().Name}: {ex.Message} — falling back to balloon tip");
            if (triedToast)
            {
                // Fallback — если tray icon visible, balloon tip сработает (legacy API,
                // но на Win10 часто работает даже когда WinRT подводит).
                try { BalloonTipFallback?.Invoke(title, body); }
                catch (Exception fex) { _log?.Invoke($"balloon_fallback_failed: {fex.Message}"); }
            }
        }
    }
}
