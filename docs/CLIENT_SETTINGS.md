# Настройки клиента (MVP)

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

- `RequireConfirmation` — запрашивать подтверждение подключения.
- `AllowUnattended` — unattended доступ.
- Клиент запускается с UAC-повышением (`requireAdministrator`).
- Вкладка `Настройки -> Система -> debug-log`:
  - `DebugLogDataChannelInputEnabled`
  - `DebugLogClipboardEnabled`
  - `DebugLogSignalingEnabled`
  - `DebugLogWebRtcEnabled`
- В `debug-log` есть переключатель политики `PromptOnSecureDesktop`:
  - `0` — UAC-окна видны в удаленной сессии (менее безопасно)
  - `1` — стандартный Secure Desktop (безопаснее)

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

## 5) Сохранение

Настройки сохраняются в локальный файл, например:

- `%AppData%\\ZConect\\client-settings.json`

Секреты (`TurnPassword`) хранятся в защищенном хранилище при переходе к production версии.
