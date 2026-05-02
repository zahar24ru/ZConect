# Signaling Server

Go 1.25 | Alpine 3.21 | SQLite (telemetry)

## Запуск локально

```bash
go run ./cmd/signaling
```

По умолчанию слушает `:8080`.

## Переменные окружения

| Variable | Default | Description |
|---|---|---|
| `SIGNALING_PORT` | `8080` | HTTP/WS listen port |
| `SESSION_TTL_SEC` | `300` | Session lifetime in seconds |
| `MAX_JOIN_ATTEMPTS` | `5` | Failed join attempts before lockout |
| `LOCK_MINUTES` | `10` | Lockout duration |
| `RATE_LIMIT_PER_MIN` | `200` | Max API requests per IP per minute |
| `DASHBOARD_API_KEY` | _(empty)_ | API key for dashboard access (empty = disabled) |
| `TELEMETRY_DB_PATH` | `telemetry.db` | SQLite database path |
| `ALLOWED_ORIGINS` | _(empty)_ | Comma-separated WS origins (empty = allow all in dev) |
| `TRUSTED_PROXIES` | _(empty)_ | IPs allowed to set X-Forwarded-For |
| `APP_ENV` | `dev` | `prod` or `dev` |

## Endpoints

### API
- `GET /healthz` — health check
- `POST /api/v1/session/create` — create session
- `POST /api/v1/session/join` — join with login+pass codes
- `POST /api/v1/session/close` — close session (requires owner_secret)
- `POST /api/v1/session/refresh` — extend TTL / regenerate pass
- `POST /api/v1/telemetry/heartbeat` — client heartbeat (machine_id, OS, version)

### Dashboard
- `GET /dashboard?key=XXX` — web dashboard (telemetry)
- `GET /api/v1/telemetry/stats?key=XXX` — JSON stats

### WebSocket
- `GET /ws?session_id=...` — signaling relay (token sent via auth message, not URL)

## Security

- WS token: HMAC-SHA256, 60s TTL, sent in first `auth` message (NET-06)
- HTTP body limit: 4 KB on all API endpoints (N7-03)
- Expired/closed sessions rejected before WS upgrade (N7-01)
- Write deadline 5s per peer (slow-receiver DoS, N7-02)
- Max 3 peers per session (reconnect overlap, stale peers auto-cleanup)
- Per-peer WS rate limit: 30 msg/sec, 60 burst
- Reserved message types blocked from clients (NET-01)

## Logs

JSONL format in `logs.log` with auto-rotation (5 MB, 3 backups).

## Telemetry

SQLite database stores client heartbeats (machine_id, OS, language, version, country via GeoIP).
Dashboard shows: online count, active sessions/peers, total installs, OS/language/country distribution.
