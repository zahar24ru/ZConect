namespace UiApp.Models;

public enum ConnectionState
{
    /// <summary>Нет активной сессии.</summary>
    Idle,
    /// <summary>Идёт подключение / ICE / согласование.</summary>
    Connecting,
    /// <summary>Соединение установлено.</summary>
    Connected,
    /// <summary>Ошибка или разрыв.</summary>
    Error
}
