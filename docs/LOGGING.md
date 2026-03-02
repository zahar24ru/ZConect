# Логирование (`logs.log`)

## 1) Формат

Используется JSON Lines (одна JSON запись на строку) в файле `logs.log`.

Текущий клиентский формат:

```json
{"ts":"2026-02-26T14:41:44.5505712Z","level":"DEBUG","module":"DataChannel","event_name":"dc_sent:Input:mouse_input","error":null}
{"ts":"2026-02-26T14:41:53.5984182Z","level":"DEBUG","module":"ClipboardSync","event_name":"clipboard_sent_len_7","error":null}
{"ts":"2026-02-26T14:39:24.4233323Z","level":"INFO","module":"RemoteScreen","event_name":"first_remote_video_frame_received","error":null}
```

## 2) Обязательные поля

- `ts` — timestamp UTC ISO8601.
- `level` — `DEBUG|INFO|WARN|ERROR`.
- `module` — имя модуля.
- `event_name` — имя события.
- `error` — текст ошибки или `null`.

Рекомендуемые поля:

- `session_id`
- `peer_id`
- `trace_id`
- `ip` (для сервера)

## 3) Ротация

- Текущий файл: `logs.log`
- Ротация по размеру: 10 MB
- Храним 5 архивов: `logs.1.log ... logs.5.log`

## 4) Принципы

- Не логировать пароли/секреты/полные токены.
- Для ошибок сохранять код и контекст.
- Критические ошибки дублировать в stderr.

## 5) Фильтрация DEBUG в UI

В `Настройки -> Система -> debug-log` можно включать/выключать шумные DEBUG-категории:

- `DataChannel Input` (`dc_sent:Input:*`, `dc_recv:Input:*`)
- `Clipboard` (`ClipboardSync` и clipboard-события `DataChannel`)
- `Signaling + WS` (`Signaling`, `UiApp/ws_message_*`)
- `WebRTC`

Фильтрация применяется в клиентском `LogService` до записи в `logs.log`.
