# API & Signaling Protocol

Последнее обновление: 2026-04-22.

**Scope:** внешние protocols клиента:
- HTTP REST API к сигналинг-серверу
- WebSocket signaling (offer/answer/ICE)
- Data channels поверх WebRTC SCTP

**Внутренний IPC** (Named Pipe между service/UI/InputHelper) описан отдельно в [PIPE_PROTOCOL.md](PIPE_PROTOCOL.md).

## 1) HTTP API

Base prefix: `/api/v1`

### `POST /api/v1/session/create`

Creates a new session and returns codes + ownership credentials.

Request:

```json
{
  "request_unattended": true,
  "expires_in_sec": 86400,
  "machine_id": "optional-persistent-device-id",
  "device_secret": "optional-proof-of-device-ownership"
}
```

Response:

```json
{
  "session_id": "9c4f2a61-59b8-4e02-83ea-c6ac2b8f5db2",
  "login_code": "12345678",
  "pass_code": "87654321",
  "expires_in_sec": 300,
  "ws_token": "hmac-signed-token",
  "ws_url": "/ws",
  "owner_secret": "hex-random-id-for-close-refresh",
  "device_secret": "hex-random-id-for-machine-binding",
  "turn_servers": [
    {
      "urls": ["turn:turn.example.com:3478?transport=udp"],
      "username": "1714123456:9c4f2a61-59b8-4e02-83ea-c6ac2b8f5db2",
      "credential": "vZhfmN8oRPaQqjL8n5A3dX2y4kZ=",
      "expires_at_unix": 1714125256,
      "ttl_seconds": 1800
    }
  ]
}
```

Notes:
- `owner_secret` — returned only to the session creator (host). Required for close/refresh.
- `device_secret` — generated on first create with `machine_id`. Required to reuse an existing session via `machine_id`.
- If `machine_id` + `device_secret` match an existing active session, returns that session (no new codes).
- `turn_servers` — **rotating TURN credentials (RFC 7635 / coturn `--use-auth-secret`)**. Optional, absent если `TURN_AUTH_SECRET` не задан в server's .env (legacy mode).
  - `username` — формата `<exp_ts>:<session_id>` где `exp_ts` — Unix seconds когда credential expired
  - `credential` — base64(HMAC-SHA1(shared_secret, username))
  - Client должен сделать `/session/refresh` до `expires_at_unix` чтобы получить свежие creds. Default TTL = 30 мин (настраивается через `TURN_CRED_TTL_SEC`).
  - Если `turn_servers` present — client должен использовать именно эти creds для TURN auth (не `TurnUsername`/`TurnPassword` из своих settings). STUN URL можно использовать свой.
  - Подробнее: [`TURN_ROTATION.md`](./TURN_ROTATION.md).

### `POST /api/v1/session/join`

Join an existing session with codes.

Request:

```json
{
  "login_code": "12345678",
  "pass_code": "87654321"
}
```

Response (200 OK):

```json
{
  "session_id": "9c4f2a61-...",
  "require_confirm": false,
  "state": "PAIRING",
  "ws_url": "/ws",
  "ws_token": "hmac-signed-token"
}
```

Error responses (unified to prevent login enumeration — NET-02):
- `401 Unauthorized` — invalid credentials (same for wrong login, wrong pass)
- `403 Forbidden` — IP is banned (admin-configured via `/admin/api/ban`); body `{"error":"banned"}`
- `423 Locked` — session permanently blocked (StateBlocked) after 20 cumulative wrong
  passcode attempts across all lockout tiers. Host must refresh password to unblock.
  Body `{"error":"session blocked"}`.
- `429 Too Many Requests` — session temporarily locked (current tier lockout).
  Includes `Retry-After: N` header + body `{"error":"session locked","retry_after_sec":N}`.
  Progressive tiers: 15s → 2m → 15m → 1h, then → StateBlocked on next 5 fails.
- `503 Service Unavailable` — server in maintenance mode
  (`/admin/api/maintenance` ON)

See [client error handling](#5-client-error-handling) для полного mapping'а на UX.

### `POST /api/v1/session/close`

Terminates a session. Requires owner_secret (F-03).

Request:

```json
{
  "session_id": "9c4f2a61-...",
  "owner_secret": "hex-random-id"
}
```

Response: `200 {"ok": true}` or `403 {"error": "forbidden"}`

### `POST /api/v1/session/refresh`

Extends session TTL and optionally regenerates pass_code. Requires owner_secret.

Request:

```json
{
  "session_id": "9c4f2a61-...",
  "owner_secret": "hex-random-id",
  "expires_in_sec": 3600,
  "regenerate_pass": false
}
```

Response:

```json
{
  "session_id": "9c4f2a61-...",
  "login_code": "12345678",
  "pass_code": "87654321",
  "expires_in_sec": 3600,
  "ws_token": "fresh-hmac-token"
}
```

Notes:
- `regenerate_pass: false` (default) — keeps existing pass_code (used for WS reconnect)
- `regenerate_pass: true` — generates new pass_code (manual refresh / auto-refresh timer)

### `GET /healthz`

Returns `{"status": "ok"}`.

### `POST /api/v1/telemetry/heartbeat`

Client sends periodic heartbeat with system info (every 5 min).

Request:

```json
{
  "machine_id": "guid",
  "app_version": "1.0.0",
  "os_version": "Microsoft Windows 10.0.26100",
  "os_language": "ru-RU"
}
```

Response: `200 {"ok": true}`

### `GET /api/v1/telemetry/stats?key=XXX`

Returns aggregated telemetry data (protected by `DASHBOARD_API_KEY`).

### `GET /dashboard?key=XXX`

Web dashboard with auto-refresh (protected by `DASHBOARD_API_KEY`).

### Admin panel endpoints

Session-based auth (bcrypt + cookie). Admin UI at `/admin` (login at `/admin/login`).
Все endpoints requires session cookie; state-changing endpoints (POST) require
CSRF token header `X-CSRF-Token`.

Key endpoints:
- `POST /admin/api/login` / `POST /admin/api/logout` / `GET /admin/api/session`
- `GET /admin/api/health` — runtime stats (uptime, sessions, peers, goroutines)
- `GET /admin/api/webrtc-sessions` — list активных sessions с brute-force state
  (total_failed_attempts, lockout_tier, locked_until_unix_sec) + `Kill` action
- `POST /admin/api/webrtc-sessions/kill` — force-close session
- `GET /admin/api/suspicious-ips?min=N` — per-IP enumeration counter
  (для обнаружения attacker'ов с несуществующими login_codes). Sliding 5-min window.
  Response: `{"ips":[{ip,count,first_seen,last_seen}],"count":N,"window_sec":300}`
- `POST /admin/api/ban` / `GET /admin/api/ban/list` — IP blocklist
  (supports TTL через AutoBan infrastructure, UI — manual only)
- `POST /admin/api/maintenance` — toggle maintenance mode
- `GET /admin/api/audit` / `/logs` — tail audit NDJSON / server logs
- `POST /admin/api/ratelimit/reset` — clear login rate limiter
- `POST /admin/api/password` — change admin password (persistent hash file)
- `POST /admin/api/sessions/logout-all` — revoke other admin sessions
- `GET/POST /admin/api/version` — edit `client-version.json` for auto-update
- `GET /admin/api/backup/db` — download `telemetry.db`

## 2) WebSocket Signaling

Endpoint: `/ws?session_id=...`

Connection flow (NET-06):
1. Client connects to `/ws?session_id=XXX` (token NOT in URL)
2. Client sends first message: `{"type":"auth","session_id":"XXX","payload":{"token":"hmac-token"}}`
3. Server validates token (HMAC-SHA256, 60-second TTL)
4. If valid, peer is added to session; if invalid, connection is closed

Limits:
- Max 3 peers per session (allows overlap during reconnect; stale peers auto-cleanup)
- Per-peer rate limit: 30 msg/sec, 60 burst
- Max message size: 64 KB
- N7-01: Expired/closed sessions rejected before upgrade
- N7-02: 5-second write deadline per peer (slow-receiver DoS protection)
- N7-03: HTTP body size limit 4 KB on all API endpoints

### Message validation (NET-01)

Server blocks reserved types from clients:
- `peer_disconnected` — server-only (sent when a peer disconnects)
- `error`, `system_error`, `system_notice` — server-only
- `auth` — consumed by server, never relayed

All other message types are relayed to the other peer(s).

### Message types

Envelope:

```json
{
  "type": "offer|answer|ice|peer_state",
  "session_id": "uuid",
  "payload": {}
}
```

- `offer` / `answer` — SDP exchange
- `ice` — ICE candidate: `{ "candidate": "...", "sdpMid": "0", "sdpMLineIndex": 0 }`
- `peer_state` — connection state: `{ "state": "host_ready|joined" }`
- `peer_disconnected` — server-generated: `{ "peerId": "hex" }`
- `ping` / `pong` — RTT measurement

## 3) Security

| Parameter | Value |
|---|---|
| Session TTL | 300s default, max 7 days |
| Join attempts per tier | 5 max, then progressive tier lockout (см. §3.1) |
| WS token TTL | 60 seconds (HMAC-SHA256) |
| Origin check | Deny in prod if no `ALLOWED_ORIGINS` |
| Rate limit | 200 req/min per IP default (`RATE_LIMIT_PER_MIN`), 30 msg/sec per peer (WS) |
| HTTP body limit | 4 KB max on all API endpoints (N7-03) |
| Dashboard auth | `DASHBOARD_API_KEY` env var (empty = dashboard disabled) |
| Admin auth | `ADMIN_PASSWORD_HASH` bcrypt + session cookie + CSRF token |
| XFF trust | Only from `TRUSTED_PROXIES` IPs |

### 3.1) Session brute-force protection (per-session, not per-IP)

Per-session progressive lockout. Per-IP ban **не автоматический** (manual admin
action) — NAT users могут many legitimate connections от one IP.

Tier progression (MaxJoinAttempts=5 wrong attempts per tier):

| Tier | Duration | Status | Cumulative fails |
|---|---|---|---|
| 0 | — | Normal (no lock) | 0-4 |
| 1 | **15 сек** | LockedUntil (429) | 5-9 |
| 2 | **2 минуты** | LockedUntil (429) | 10-14 |
| 3 | **15 минут** | LockedUntil (429) | 15-19 |
| 4 | **1 час** | LockedUntil (429) | 20-24 |
| 5+ | **permanent** | StateBlocked (423) | 25+ |

- `TotalFailedAttempts` — cumulative, не ресетится на lock; ресет на success join
  или password refresh (`regenerate_pass=true`)
- `LockRemaining(login_code)` — API computes `Retry-After` seconds для 429 response
- Host unblock'ает StateBlocked через `POST /session/refresh` с `regenerate_pass=true`

### 3.2) Per-IP enumeration detector (observability)

Tracker (internal/bruteforce) считает ВСЕ `session_join_failed` events per IP
в sliding 5-min window. Exposed через `/admin/api/suspicious-ips`. **Не автобан** —
admin видит IP с count'ом и решает ban через UI.

Нужно потому что session-level protection не работает при attacker'ах который
пробует несуществующие `login_code` (session lookup возвращает ErrBadCredentials
до JoinAttempts counter).

## 4) Data Channels

Four data channels over WebRTC SCTP:

| Channel | Purpose |
|---|---|
| `dc-control` | Screen meta, display selection, cursor shape, folder operations, rename |
| `dc-input` | Mouse + keyboard events |
| `dc-clipboard` | Clipboard text sync |
| `dc-file` | File transfer (meta, chunks, ack, error) |

### dc-control messages

- `screen_meta` — display resolution and position
- `host_video_settings_request` — quality/display change from viewer
- `host_displays` / `host_displays_request` — available displays list
- `cursor_shape` — cursor type sync (arrow/ibeam/hand/wait/resize/...)
- `dir_list_request` / `dir_list_response` — remote directory listing
- `file_request` — download file from host (with RequestId)
- `folder_download_request` / `folder_download_response` — recursive folder download
- `create_folder_request` / `create_folder_response` — create remote folder
- `delete_request` / `delete_response` — delete remote file/folder
- `rename_request` / `rename_response` — rename remote file/folder
- `ping` / `pong` — RTT measurement (3s interval, EMA 0.7/0.3)

### dc-file protocol

1. `file_meta` — `{ transferId, fileName, fileSize, hash, targetDirectory, relativePath, compressed }`
2. Binary chunks — `[0x01 marker][sequence int32][transferId 32 ASCII][flags][raw data]` (64 KB default)
3. `file_end` — `{ transferId }`
4. `file_ack` — `{ transferId, success, message }`
5. `file_error` — `{ transferId, code, message }`

Codes: `cancelled`, `send_error`, `receive_error`, `chunk_error`, `path_rejected`, `hash_mismatch`, `access_denied`, `not_found`

### dc-clipboard protocol

- `clipboard_text` — UTF-8 text, max 256 KB
- Poll interval: 250ms
- Echo protection: ignore recently applied remote text
- Init message `zconect-init` excluded from system clipboard

## 5) Client error handling

`SessionApiClient` возвращает `CreateSessionResult` / `JoinSessionResult` с
typed status'ом, чтобы ViewModel мог показать правильное сообщение user'у.

### CreateSessionStatus (POST /session/create)

| HTTP | Status | UI behavior |
|---|---|---|
| 200 | `Success` | Создать сессию, показать коды |
| 403 | `Banned` | MessageBox + status bar: «Ваш IP заблокирован» |
| 503 | `Maintenance` | MessageBox + status: «Сервер на тех. обслуживании» |
| 5xx | `ServerError` | Status bar: «Ошибка сервера» |
| (exception) | `NetworkError` | Status bar: «Нет связи с сервером» |

### JoinSessionStatus (POST /session/join)

| HTTP | Status | RetryAfterSec | UI behavior |
|---|---|---|---|
| 200 | `Success` | — | Подключаемся к WS |
| 401 | `InvalidCredentials` | 0 | Status bar (без MessageBox, typos don't warrant dialog) |
| 403 | `Banned` | 0 | MessageBox + status |
| 423 | `SessionBlocked` | 0 | MessageBox: «Сессия заблокирована, host должен обновить пароль» |
| 429 | `SessionLocked` | >0 | Status bar с countdown: «Попробуйте через N сек» |
| 5xx | `ServerError` | 0 | Status bar |

`RetryAfterSec` читается сначала из `Retry-After` HTTP header, fallback на
`retry_after_sec` в response body. Все сообщения локализованы (ru/en) через
`Strings.Status_*` keys.
