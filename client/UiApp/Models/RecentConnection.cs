namespace UiApp.Models;

/// <summary>
/// Запись в истории недавних подключений. Хранится отдельно от <see cref="Contact"/>
/// в <c>%AppData%\ZConect\recent-history.json</c>. После успешного ad-hoc connect
/// (введённый login/pass) запись попадает сюда — НЕ в Address Book. Пользователь
/// может явно «сохранить» историю-запись как настоящий Contact через иконку в Recent
/// card, тогда она удаляется из history и добавляется в <see cref="Contact"/>.
///
/// Соответственно, Recent row показывает union:
///   - Contacts с недавним LastConnectedUtc (именованные, «полноценные»)
///   - RecentConnection записи (временные, ephemeral — «?» avatar в UI)
/// dedup по LoginCode: если есть Contact — он превалирует.
/// </summary>
public sealed class RecentConnection
{
    /// <summary>8-значный login session code сервера.</summary>
    public string LoginCode { get; set; } = string.Empty;

    /// <summary>8-значный pass session code. Short-lived (истекает с TTL сессии),
    /// поэтому DPAPI-encryption не делаем — если сохранить надолго ценности нет.</summary>
    public string PassCode { get; set; } = string.Empty;

    /// <summary>UTC timestamp последнего connect. Для сортировки Recent row.</summary>
    public DateTime LastConnectedUtc { get; set; }
}
