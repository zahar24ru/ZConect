# Клиент Windows (план модулей)

Цель: легковесный клиент для Windows 10/11 с простым UI, полным удаленным управлением и поддержкой WebRTC.

## Модули

- `UiApp` — главное окно и настройки.
- `SessionClient` — REST/WS клиент сигналинга.
- `WebRtcTransport` — peer connection, медиа и data channels.
- `ScreenCapture` — выбор и захват экранов.
- `InputHost` — мышь/клавиатура.
- `QualityController` — профили качества.
- `ClipboardSync` — буфер обмена.
- `FileTransfer` — простой обмен файлами.
- `Logging` — запись в `logs.log`.

## MVP UX

- Кнопка "Подключиться"
- Поля "Логин (8 цифр)" / "Пароль (8 цифр)"
- Переключатель "Запрашивать подтверждение"
- Выбор экрана: "Текущий / Любой / Все"
- Качество: Низкое / Среднее / Высокое / Авто

## Текущий статус реализации

- Создан WPF каркас `client/UiApp`.
- Реализовано главное окно с блоками:
  - быстрое подключение (создать/подключиться)
  - логин/пароль 8+8
  - toggles подтверждения и unattended
  - качество и режим экрана
  - сетевые настройки (API/WS/STUN/TURN)
  - путь к `ffmpeg.exe`
- Настройки сохраняются в `%AppData%\ZConect\client-settings.json`.
- Логи UI пишутся в `logs.log` рядом с исполняемым файлом.

Далее: подключение `SessionClient` к серверным endpoint и реальный вызов `/api/v1/session/create|join`.

## Обновление статуса

Сделано:

- `SessionClient` реализован и подключен к `UiApp`.
- Кнопки:
  - `Создать сессию` вызывает `POST /api/v1/session/create`
  - `Подключиться` вызывает `POST /api/v1/session/join`
- Возвращенные коды логин/пароль автоматически отображаются в UI.
- Сборка `UiApp.sln` успешна (`0 errors`, `0 warnings`).
- Добавлен `WebSocketSignalingClient` в `client/WebRtcTransport`.
- После `join` приложение автоматически подключается к `/ws`.
- Добавлены реальные агенты WebRTC:
  - `MixedRealityPeerConnectionAgent`
  - `MixedRealityDataChannelAgent`
- При недоступности native WebRTC автоматически включается mock fallback.
- Добавлены:
  - `QualityController` с профилями качества
  - `ScreenCapture` с `FfmpegVp8ProbeService`
- В UI добавлена кнопка `VP8 Probe` для проверки пути к `ffmpeg.exe` и VP8 encode.
