# Настройки клиента

Последнее обновление: 2026-04-22.

## 1) Подключение к серверу

- `ServerApiBaseUrl` — адрес API сигналинга.
  - Пример: `https://zconect.example.com`
  - Тестовый пример: `http://203.0.113.10:8080`
- `WebSocketUrl` — адрес WS endpoint.
  - Пример: `wss://zconect.example.com/ws`

## 2) WebRTC ICE

- `StunUrl` — `stun:<IP>:<PORT>`
- `TurnUrl` — `turn:<IP>:<PORT>`
- `TurnUsername`
- `TurnPassword`
- `PreferRelay` — использовать relay как приоритет.
- `PreferLanVpnNoTurn` — режим LAN/VPN без TURN.
- `AutoIceByPriority` — авто-перебор ICE этапов на viewer:
  - LAN `3с` -> srflx `4с` -> relay `5с`.

Пример:

```json
{
  "stun_url": "stun:203.0.113.10:3478",
  "turn_url": "turn:203.0.113.10:3478",
  "turn_username": "zconect",
  "turn_password": "change_me",
  "prefer_relay": false
}
```

## 3) Кодек и FFmpeg

- `Codec` — в MVP фиксировано `VP8`.
- `FfmpegPath` — путь к `ffmpeg.exe`.
  - Пример: `C:\\Tools\\ffmpeg\\bin\\ffmpeg.exe`
- Проверка: файл должен существовать и быть запускаемым.
- В текущей реализации `ffmpeg.exe` не обязателен для основного WebRTC стрима;
  используется для `VP8 Probe`.

## 3.1) Где находятся настройки в UI

- Основные сетевые и качественные настройки вынесены в окно:
  - `Настройки -> Система`
- В окне удаленного экрана (`viewer`) есть быстрые controls:
  - `QualityPreset`
  - `DisplayMode`
  - `DisplayId` (выпадающий список обнаруженных дисплеев)
  - `Применить` (on-the-fly)
  - `Быстрый реконнект` (быстрая renegotiation на host)
- В `Настройки -> Система` поле `DisplayId` также выбрано через выпадающий список.
- Если список дисплеев не получен, используется fallback `DISPLAY1`.

## 4) UX и безопасность

- `UiLanguage` — код культуры для UI: `"ru-RU"`, `"en-US"` или пустая строка (system default). На первом запуске, если пустая и existует seed-файл `C:\ProgramData\ZConect\initial-language.txt` (создаётся installer'ом), значение адоптируется оттуда. Изменение через Настройки → Приложение → Язык; применяется на restart. Детали: `docs/LOCALIZATION.md`.
- `ThemeMode` — `"Dark"` или `"Light"` (последнее значение кнопки-переключателя темы; persist между сессиями).
- `RequireConfirmation` — запрашивать подтверждение подключения viewer'а (host-side dialog).
- `AllowUnattended` — unattended доступ (persistent session через `machine_id` + `device_secret`).
- Клиент запускается как **`asInvoker`** (user token, Medium integrity). После Stage 1 refactor (2026-04-18) UI больше не требует admin elevation. UAC-требующие операции (HKLM write, Winlogon desktop clicks) делегируются через named pipe в service (SYSTEM) и InputHelper (winlogon token).
- Вкладка `Настройки → Система → debug-log`:
  - `DebugLogDataChannelInputEnabled`
  - `DebugLogClipboardEnabled`
  - `DebugLogSignalingEnabled`
  - `DebugLogWebRtcEnabled`
- Переключатель политики `PromptOnSecureDesktop`:
  - `0` — UAC-окна видны в удалённой сессии (менее безопасно для локального ПК, но viewer видит UAC и может реально кликнуть)
  - `1` — стандартный Secure Desktop (безопаснее, но UAC не виден viewer'у)
  - Переключение делегируется в service через pipe (`set_uac_policy` message) — UI под user token сам HKLM писать не может.

## 4.6) Client error handling (server rejections)

`SessionApiClient` (namespace `SessionClient`) раскладывает HTTP response'ы от
signaling server'а в typed result'ы — `CreateSessionResult` / `JoinSessionResult`
с enum'ом status'а. `MainViewModel.Connection.cs` показывает user-facing сообщения
в зависимости от причины.

### CreateSessionStatus (POST /session/create)

| Status | HTTP | UI |
|---|---|---|
| `Success` | 200 | Создать сессию, показать коды |
| `Banned` | 403 | MessageBox + status: «Ваш IP заблокирован» / «Your IP is banned» |
| `Maintenance` | 503 | MessageBox + status: «Сервер на тех. обслуживании» |
| `NetworkError` | (exc) | Status bar: «Нет связи с сервером» |
| `ServerError` | 5xx | Status bar: «Ошибка сервера» |
| `InvalidResponse` | 200-badbody | Status bar: generic error |

### JoinSessionStatus (POST /session/join)

| Status | HTTP | RetryAfter | UI |
|---|---|---|---|
| `Success` | 200 | — | Подключаемся к WS |
| `InvalidCredentials` | 401 | 0 | Status bar (без MessageBox — typos don't warrant dialog) |
| `Banned` | 403 | 0 | MessageBox + status |
| `SessionBlocked` | 423 | 0 | MessageBox: «Сессия заблокирована, host должен обновить пароль» |
| `SessionLocked` | 429 | >0 | Status bar countdown: «Попробуйте через N сек» (Retry-After header) |
| `NetworkError` | (exc) | 0 | Status bar |
| `ServerError` | 5xx | 0 | Status bar |

`RetryAfterSec` парсится сначала из `Retry-After` HTTP header (RFC 7231 standard),
fallback на `retry_after_sec` поле в response body. Все сообщения локализованы
через `Strings.Status_*` keys (ru + en). Детали: [API.md §5](API.md#5-client-error-handling).

## 4.1) Clipboard (текущее состояние)

- Реализован `dc-clipboard` для текста (host <-> viewer).
- Лимит размера текста: 256 KB (UTF-8).
- Служебный `zconect-init` не пишется в системный clipboard.

## 4.2) File transfer (текущее состояние)

- В главном окне есть кнопка `Отправить файл`.
- Передача работает в обе стороны (host/viewer) по `dc-file`.
- В UI отображается прогресс передачи в процентах.
- Полученный файл автоматически сохраняется в:
  - `%UserProfile%\\Downloads\\ZConectReceived`
- Для приема используется временная папка:
  - `%LocalAppData%\\ZConect\\IncomingTemp`

### Security model для FT

**⚠️ Viewer имеет полный read/write доступ к user profile** того пользователя, кто даёт remote session. FT handlers блокируют только **системные пути** (`C:\Windows`, `C:\Program Files`, `C:\ProgramData` и т.п.) через `SafePath.IsSystemPath` — всё user-level доступно.

**Почему так**:
- В remote desktop model viewer уже может mouse/keyboard в любом окне/документе host'а
- Ограничивать FT к root-jail'у создаёт false sense of security — viewer просто скопирует файл через clipboard paste или drag-drop в remote Проводник
- Host всё равно решает кого пускать через `RequireConfirmation` диалог при connect'е

**Legacy fields в ClientSettings.json** (устарели, не используются):
- `FtRootJailPath` — был в UI idea, никогда не реализован в handlers. JSON deserialization safely игнорирует.

## 4.3) ICE статус и диагностика

- В главном окне показывается `ICE: <route> [hint]` с `local/remote` типом и IP.
- В авто-режиме ICE кандидаты фильтруются по этапам (`host -> srflx -> relay`) для приоритета более прямого канала.
- Цвет статуса:
  - `host` — зеленый
  - `srflx` — желтый
  - `relay` — оранжевый
- Ниже выводится debug-список последних 5 ICE кандидатов (`time direction:type ip:port`).

## 4.5) Синхронизация курсора

- В удалённом окне viewer применяется форма курсора host.
- Передаются типы: `arrow/ibeam/hand/resize/wait/...` через `dc-control` событие `cursor_shape`.

## 4.4) Закрытие сессии

- При штатном закрытии приложения клиент вызывает `POST /api/v1/session/close` для текущей `session_id`.
- После этого старые `login/pass` должны считаться закрытыми и требуется создать новую сессию.

## 5) Telemetry

- Клиент автоматически отправляет heartbeat на сервер каждые 5 минут.
- Данные: machine_id, app_version (1.0.0), OS version, UI language.
- Ошибки телеметрии глотаются — не влияет на работу приложения.
- Отключить нельзя (пока), но данные минимальны и анонимны.

## 6) Сохранение

### UI settings
**Path:** `%AppData%\ZConect\client-settings.json` (user-scope)

- **Атомарная запись** (2026-04-18): `SettingsService.Save` пишет в `.tmp` → `File.Move(overwrite:true)` — NTFS-atomic rename. Concurrent Load во время Save не увидит truncated JSON.
- **Backup**: перед write делается `File.Copy` основного файла в `.bak`. При corruption основного файла `Load` fallback'ает на `.bak`.
- **DPAPI CurrentUser** для секретов: `TurnPassword`, `LastPassCode`, `LastLoginCode`.
- **DPAPI Migration fallback**: если CurrentUser decrypt fails (старые версии ZConect до Stage 1 запускались под SYSTEM token → LocalMachine scope), `DpapiHelper.Decrypt` пробует LocalMachine. Успех → plaintext в памяти; на следующем `Save()` `EncryptSecrets` re-зашифрует уже CurrentUser. Self-migration без user action.

### Service settings
**Path:** `C:\ProgramData\ZConect\service-config.json` (SYSTEM-owned)

- **DPAPI LocalMachine** для `TurnPassword`, `DeviceSecret`, `UnattendedPassword` — читается только SYSTEM / admins на этой машине; не переносится на другие.
- Service encrypt/decrypt — в `ServiceConfig.Load/Save`.
- UI не читает этот файл напрямую — получает relevant поля через `config` pipe message.

### Address book
**Path:** `%AppData%\ZConect\address-book.json` (user-scope, plaintext — нет секретов).

## 7) Версия

App version: `1.0.0` (задана в UiApp.csproj, доступна через Assembly.GetName().Version).
