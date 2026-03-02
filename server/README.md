# Сервер сигналинга (MVP)

## Запуск локально

```bash
go run ./cmd/signaling
```

По умолчанию слушает `:8080`.

## Переменные окружения

- `SIGNALING_PORT` (default: `8080`)
- `SESSION_TTL_SEC` (default: `300`)
- `MAX_JOIN_ATTEMPTS` (default: `5`)
- `LOCK_MINUTES` (default: `10`)

## Эндпоинты

- `GET /healthz`
- `POST /api/v1/session/create`
- `POST /api/v1/session/join`
- `POST /api/v1/session/close`
- `GET /ws?session_id=...` — WebSocket для обмена SDP/ICE между peer

## Логи

Сервер пишет JSONL записи в `logs.log` в рабочем каталоге процесса.

## Ограничения текущего этапа

- Хранилище сессий in-memory (Redis в docker-compose пока не используется в коде).
- `ws_token` пока заглушка.
