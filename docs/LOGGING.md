# Логирование

Последнее обновление: 2026-04-22.

## 1) Формат

JSON Lines (одна JSON запись на строку).

**Клиентский формат** (UI, Service, InputHelper):
```json
{"ts":"2026-04-18T13:01:45.7516467Z","level":"INFO","module":"UiApp","event_name":"ws_connected","error":null}
```

**Серверный формат** (Go signaling):
```json
{"ts":"2026-04-18T13:01:45.698Z","level":"INFO","module":"Main","event":"server_start","message":"signaling server started"}
```

## 2) Обязательные поля

| Поле | Описание |
|---|---|
| `ts` | Timestamp UTC ISO8601 |
| `level` | `DEBUG`, `INFO`, `WARN`, `ERROR` |
| `module` | Имя модуля (UiApp, WebRTC, Signaling, Pipe, InputHost, InputHelper, API, WS, Session, Telemetry) |
| `event_name` / `event` | Имя события |
| `error` | Текст ошибки или null |
| `session_id` | (сервер) ID сессии |
| `ip` | (сервер) IP клиента |

## 3) Пути к лог-файлам

| Процесс | Путь |
|---|---|
| UI (`ZConnect.exe`) | `%LocalAppData%\ZConect\logs\ui.log` |
| Service (`ZConectService.exe`) | `C:\ProgramData\ZConect\logs\service.log` |
| InputHelper (`ZConectInputHelper.exe`) | `C:\ProgramData\ZConect\logs\input-helper.log` |
| Signaling server (Docker) | `/app/logs.log` внутри контейнера |

## 4) Ротация

Все 4 log-pipeline'а используют rotation 5 MB × 3 backups (`.log.1`, `.log.2`, `.log.3`):

| Компонент | Реализация |
|---|---|
| UI `LogService` | StreamWriter + lock; check size перед write; rename shift |
| Service `ServiceLogger` | StreamWriter + lock; rotation на write |
| InputHelper | `RotateLogIfNeeded` перед каждым AppendAllText (добавлено 2026-04-18, commit d94cadf — раньше было unbounded growth) |
| Go server | `internal/logging` — lumberjack-style rotation |

На shift (когда `.log` превышает 5 MB):
- `.log.3` удаляется (oldest)
- `.log.2` → `.log.3`
- `.log.1` → `.log.2`
- `.log` → `.log.1`
- Новый `.log` создаётся при next write

## 5) Безопасность логов

- **Коды сессий маскируются**: `pass=****XXXX`, `login=****XXXX` (последние 4 цифры) — применяется в service + unattended + UI logs
- **WS token НЕ логируется** — передаётся в auth-сообщении (не в URL)
- **owner_secret, device_secret, turn_password НЕ логируются**
- **Presence device_secret** логируется как bool: `has_device_secret=True`
- **SID в pipe verification** маскируется: `S-1-5-21-*-*-*-1001` (оставляется только RID)

## 6) Фильтрация DEBUG в UI

В `Настройки → Система → debug-log` можно включать/выключать шумные категории:

- `DataChannel Input` (`dc_sent:Input:*`, `dc_recv:Input:*`)
- `Clipboard` (`ClipboardSync` + clipboard events)
- `Signaling + WS` (`Signaling`, `UiApp/ws_message_*`)
- `WebRTC`

Фильтрация применяется в `LogService` до записи в файл.

InputHelper / Service такой UI-фильтрации не имеют — всегда пишут на уровне категории по коду.

## 7) Известные log events (ключевые)

### Pipe module (service)

| Event | Level | Meaning |
|---|---|---|
| `waiting_for_helper_connection` | DEBUG | Pipe server ждёт connect |
| `helper_connected` | INFO | UI подключился |
| `helper_connected_sid_verified` | INFO | SID check passed |
| `pipe_sid_verified <S-1-5-21-*-*-*-XXXX>` | DEBUG | SID successfully verified |
| `pipe_sid_mismatch client=<SID> active_console=<SID>` | WARN | Rejected — другой user |
| `pipe_sid_no_active_console` | WARN | Boot/logout moment, нет active session |
| `config_sent login=****XXXX` | INFO | Config delivered |
| `turn_password_not_configured_stun_only` | WARN | TURN auth не будет работать |
| `config_push_pipe_disposed_race` | DEBUG | Expected race на PushConfigToHelperAsync |
| `desktop_changed_pushed to=X` | DEBUG | Relay из helper в UI |
| `helper_disconnected` | INFO | UI pipe closed normally |

### InputHelper module (service's view)

| Event | Level | Meaning |
|---|---|---|
| `helper_spawned pid=X session=Y` | INFO | InputHelper.exe launched |
| `pipe_connected_to_helper` | INFO | Service's helper-pipe client connected |
| `helper_pipe_disconnected` | WARN | Helper pipe closed (ManageLoop reconnect'ится) |
| `desktop_changed_from_helper to=X` | INFO | Received от helper, relay'ится в UI |
| `send_to_helper_io_error` | DEBUG | IOException на write (expected on disconnect race) |
| `send_to_helper_failed` | WARN | Unexpected exception |

### InputHelper (helper's own log)

| Event | Level | Meaning |
|---|---|---|
| `dpi_awareness_per_monitor_v2_set` | INFO | Startup DPI awareness ok |
| `helper_starting pipe=X pid=Y session=Z` | INFO | Startup |
| `token_user=S-1-5-18 name=NT AUTHORITY\СИСТЕМА` | INFO | Running under SYSTEM (winlogon token) — ok |
| `winstation_switched_to_winsta0` | INFO | SetProcessWindowStation ok |
| `desktop_monitor_started initial=Default` | INFO | Poll loop started |
| `service_connected` | INFO | Service подключился как pipe client |
| `thread_desktop_set_first_time name=Winlogon` | INFO | First successful SetThreadDesktop на Winlogon |
| `mouse action=X x=Y z=Z ok=True err=0 seq=N desktop=Default` | DEBUG | Mouse inject (logs every 60th move, all clicks, all errors) |
| `send_input_mouse_failed flags=0xX err=Y` | WARN | SendInput returned 0 (blocked — UIPI, kernel filter, UAC switch) |
| `send_input_kbd_failed vk=0xX keyup=Y err=Z` | WARN | Keyboard SendInput fail |
| `open_input_desktop_failed err=X` | WARN | OpenInputDesktop fail (rare) |
| `set_thread_desktop_failed err=X` | WARN | SetThreadDesktop fail — injection skipped (fix 2026-04-18) |

### UiApp module (UI)

| Event | Level | Meaning |
|---|---|---|
| `ws_connected` | INFO | WebSocket signaling connected |
| `join_session_clicked` | INFO | Viewer clicked Join |
| `join_success name=X` | INFO | Session joined |
| `viewer_disconnect_requested` | INFO | User closed RemoteScreenWindow |
| `uac_secure_desktop_disable_requested_via_service` | INFO | Disabled secure desktop через pipe |
| `desktop_changed_from_service from=Default to=Winlogon` | INFO | Received from service |
| `pipe_desktop_changed: Winlogon` | DEBUG | Raw message debug |
| `settings_saved path=...` | INFO | Settings atomic write complete |
| `pipe_write_io_error` | INFO | Expected pipe race (C6 handled) |

### WebRTC module

| Event | Level | Meaning |
|---|---|---|
| `remote_video_frame_N_WxH_stride_S` | DEBUG | Every 60th viewer-side frame logged |
| `ICE_STATE_CHANGED: Checking/Connected/Completed/Disconnected/Failed` | DEBUG | ICE state transitions |
| `ICE_LOCAL_CANDIDATE: host/srflx/relay mid=X` | DEBUG | ICE candidate generated |
| `auto_ice_connect_fast_rtt_Nms` | INFO | Fast ICE connect path taken |
| `auto_ice_connected_via_X` | INFO | Final ICE route type (host/srflx/relay) |
| `capture_dxgi_ok_WxH` | DEBUG | DXGI capture init succeeded |
| `capture_dxgi_multi_failed_falling_back_gdi_<err>` | WARN | DXGI failed → GDI fallback |

### API module (server, Go `internal/api`)

Log events от signaling server HTTP endpoints. Admin panel `/admin/api/logs` + `/audit` tail эти.

| Event | Level | Meaning |
|---|---|---|
| `session_created` | INFO | Новая сессия создана (`session_id` + `ip`) |
| `session_joined` | INFO | Viewer успешно залогинился (`session_id` + `ip`) |
| `session_join_failed` | WARN | Любой fail: wrong pass, wrong login_code, locked, blocked. `error` поле различает: `invalid login or password` / `session temporarily locked` / `session blocked due to repeated failed attempts` |
| `session_pass_regenerated` | INFO | Refresh с `regenerate_pass=true` — pass_code изменился (security-relevant). Ранее был misleading "session password refreshed" для обоих refresh paths |
| `session_ttl_extended` | DEBUG | Refresh с `regenerate_pass=false` (WS reconnect keepalive) — TTL extended, pass не изменился. Вынесен в DEBUG чтобы не спамить INFO log noise |
| `session_refresh_failed` | WARN | 404/403/410 — session not found / bad owner_secret / expired |

Per-IP enumeration tracker (bruteforce package) запоминает КАЖДЫЙ session_join_failed
от одного IP в sliding 5-min window → admin tab «Подозрительные IP». Не пишет
отдельных log events; snapshot только через admin API.

### Admin module (server, Go `internal/admin`)

NDJSON audit log в `/app/data/admin-audit.log` (rotate 10 MB, keep 5).

| Action | Result | Reason | Meaning |
|---|---|---|---|
| `login` | `ok` / `fail` / `denied` | `wrong_password` / `rate_limited` / `ip_not_allowed` | Login attempts |
| `logout` | `ok` | — | Session destroyed |
| `change_password` | `ok` / `fail` | `wrong_old` | Admin password rotation |
| `maintenance_mode` | `ok` | — | Toggle (metadata `enabled:true/false`) |
| `ban_add` / `ban_remove` | `ok` | — | IP blocklist (metadata `target_ip` + `reason`) |
| `reset_rate_limit` | `ok` | — | Clear login limiter |
| `download_db_backup` | `ok` | — | telemetry.db exfil |

### HealthCheck module

| Event | Level | Meaning |
|---|---|---|
| `no_pong_for_Nms_triggering_reconnect` | WARN | Control DC ping timed out → full reconnect |

### AutoQuality module

| Event | Level | Meaning |
|---|---|---|
| `auto_quality_downgrade_to_X_bitrate_N_drop_P%` | INFO | Degrade triggered |
| `auto_quality_upgrade_to_X_bitrate_N_drop_P%` | INFO | Upgrade triggered |
| `stopped_preset_not_auto_X` | INFO | User switched to manual preset |

### Unattended module (service)

| Event | Level | Meaning |
|---|---|---|
| `creating_session machine_id=X` | INFO | StartSessionAsync |
| `has_device_secret=True/False` | INFO | Startup state |
| `device_secret_saved` | INFO | Received от server на first create |
| `session_created id=X login=****Y pass=****Z` | INFO | Session ready |
| `session_refreshed login=****Y` | DEBUG | TTL extended |
| `session_refresh_failed_will_recreate` | WARN | Fell back to recreate |
