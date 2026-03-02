# API и signaling протокол (MVP)

## 1) HTTP API

Базовый префикс: `/api/v1`

### `POST /api/v1/session/create`

Создает новую сессию и возвращает пары кодов.

Request:

```json
{
  "request_unattended": true,
  "display_mode": "single",
  "display_ids": ["DISPLAY1"]
}
```

Response:

```json
{
  "session_id": "9c4f2a61-59b8-4e02-83ea-c6ac2b8f5db2",
  "login_code": "12345678",
  "pass_code": "87654321",
  "expires_in_sec": 300,
  "ws_token": "short_lived_jwt_or_random",
  "ws_url": "wss://example.com/ws"
}
```

### `POST /api/v1/session/join`

Подключение по коду.

Request:

```json
{
  "login_code": "12345678",
  "pass_code": "87654321"
}
```

Response:

```json
{
  "session_id": "9c4f2a61-59b8-4e02-83ea-c6ac2b8f5db2",
  "ws_token": "short_lived_jwt_or_random",
  "ws_url": "wss://example.com/ws"
}
```

### `POST /api/v1/session/confirm`

Используется только когда включено подтверждение доступа.

Request:

```json
{
  "session_id": "9c4f2a61-59b8-4e02-83ea-c6ac2b8f5db2",
  "allow": true
}
```

Response:

```json
{
  "ok": true
}
```

### `POST /api/v1/session/close`

Принудительно завершает сессию.

Request:

```json
{
  "session_id": "9c4f2a61-59b8-4e02-83ea-c6ac2b8f5db2"
}
```

Response:

```json
{
  "ok": true
}
```

## 2) WebSocket signaling

Endpoint: `/ws?session_id=...&token=...`

Текущий статус реализации:

- `/ws` уже реализован как relay между двумя peer внутри `session_id`.
- Поддерживается прозрачная пересылка JSON сообщений `offer/answer/ice`.
- Проверяется наличие сессии и лимит не более 2 peer на одну сессию.
- `token` пока зарезервирован под следующий этап безопасности (может быть пустым/заглушкой).

### 2.1 Типы сообщений

Общий envelope:

```json
{
  "type": "offer|answer|ice|peer_state|error|ping|pong",
  "session_id": "uuid",
  "payload": {}
}
```

#### `offer`

```json
{
  "type": "offer",
  "session_id": "uuid",
  "payload": {
    "sdp": "v=0..."
  }
}
```

#### `answer`

```json
{
  "type": "answer",
  "session_id": "uuid",
  "payload": {
    "sdp": "v=0..."
  }
}
```

#### `ice`

```json
{
  "type": "ice",
  "session_id": "uuid",
  "payload": {
    "candidate": "candidate:...",
    "sdpMid": "0",
    "sdpMLineIndex": 0
  }
}
```

#### `peer_state`

```json
{
  "type": "peer_state",
  "session_id": "uuid",
  "payload": {
    "state": "joined|left|host_ready|waiting_confirmation|confirmed"
  }
}
```

## 3) Ограничения безопасности

- TTL кодов: 300 секунд.
- Лимит попыток ввода кода/пароля: 5.
- Блокировка на 10 минут после превышения лимита.
- Коды одноразовые: после успешного join повторно не используются.
- `ws_token` краткоживущий (например, 60 секунд).

## 3.1) DataChannel control-сообщение: смена видео host

Транспорт: `dc-control`  
Тип: `host_video_settings_request`

Payload:

```json
{
  "qualityPreset": "Auto|Low|Medium|High",
  "displayMode": "Current|Any|All",
  "displayId": "DISPLAY1",
  "quickReconnect": false
}
```

Поведение:

- сообщение отправляет `viewer`;
- `host` применяет новые параметры локального захвата (качество/монитор) и отправляет обновлённый `screen_meta`;
- если `quickReconnect=true`, `host` запускает быструю renegotiation (`offer/answer`) для более надежного переключения потока.

## 3.2) DataChannel control-сообщение: курсор host

Транспорт: `dc-control`  
Тип: `cursor_shape`

Payload:

```json
{
  "cursorType": "arrow|ibeam|hand|wait|appstarting|cross|sizewe|sizens|sizenwse|sizenesw|sizeall|no"
}
```

Поведение:

- сообщение отправляет `host` при изменении формы курсора;
- `viewer` применяет ближайший локальный курсор в окне удалённого экрана.

## 4) Протокол file transfer (dc-file)

Простой протокол поверх data channel:

1. `file_meta` (имя, размер, mime, hash).
2. Серия `file_chunk` (в текущем клиенте ~16 KB до base64).
3. `file_end`.

Поддерживаем только одиночную отправку файла в MVP (без очередей и папок).

Текущий статус клиента:

- контракты `file_meta/file_chunk/file_end` реализованы в `WebRtcTransport` как типизированные payload-модели.
- двусторонняя отправка (`host <-> viewer`) активна.
- проверка hash (`SHA-256`) выполняется после приема `file_end`.
- входящий файл автоматически сохраняется в `%UserProfile%\\Downloads\\ZConectReceived`.
- во время передачи в UI отображается прогресс отправки/приема (`xx%`).

Планируемое расширение:

- `file_ack`/`file_error`
- cancel/resume

## 5) Протокол clipboard (dc-clipboard)

- Событие `clipboard_text` (UTF-8 текст).
- опрос буфера обмена с интервалом ~250 мс.
- лимит payload: 256 KB (UTF-8).
- защита от "эхо-петли" для недавно примененного удаленного текста.
- служебное init-сообщение `zconect-init` используется для прогрева канала и не должно применяться в системный clipboard.

Текущий статус клиента:

- `clipboard_text` реализован и подключен через `DataChannelCoordinator`.
- двусторонняя синхронизация `host <-> viewer` активна.
