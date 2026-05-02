# ZConnect Windows Service

Последнее обновление: 2026-04-22.

> **Scope note**: этот документ — про Windows Service (3-процессная архитектура
> service/UI/InputHelper). Server-side brute-force protection + admin panel
> endpoint'ы документированы в [API.md](API.md#31-session-brute-force-protection-per-session-not-per-ip).

## Overview

`ZConectService.exe` — Windows Service под `LocalSystem` в Session 0. Центральный элемент three-process архитектуры (подробности в [ARCHITECTURE.md](ARCHITECTURE.md#3-клиентская-архитектура--three-process)).

**Основные обязанности:**
- Владеет `service-config.json` (HKLM registry write требует SYSTEM)
- Spawn + respawn **UI helper** (`ZConnect.exe`) в активной user session
- Spawn + respawn **InputHelper** (`ZConectInputHelper.exe`) под winlogon token
- Main pipe server (`ZConect_Service_IPC`) — UI подключается для config/status/inject routing
- Helper pipe client (`ZConect_Input_Helper_IPC`) — для forwarding input в helper
- CAD via SendSAS в session 0 (`InputInjectionAgent`)
- Unattended session lifecycle через signaling API
- UAC policy write (`HKLM\...\Policies\System\PromptOnSecureDesktop`)

## Architecture

```
Session 0 (SYSTEM)                       User Session N
┌─────────────────────────────────┐      ┌──────────────────────────┐
│  ZConectService.exe             │      │  ZConnect.exe            │
│                                 │      │  (user token, Medium int) │
│  ├── ServiceWorker              │─────►│                          │
│  ├── SessionMonitor             │ spawn│  WPF UI, WebRTC,         │
│  │   (spawns UI via             │ via  │  DataChannels, clipboard,│
│  │    WTSQueryUserToken +       │ CAPU │  file transfer.          │
│  │    CreateProcessAsUser)      │      │  Connects via pipe.      │
│  ├── InputHelperManager         │      └──────────────────────────┘
│  │   (spawns InputHelper via    │      ┌──────────────────────────┐
│  │    winlogon token,           │─────►│  ZConectInputHelper.exe  │
│  │    connects as pipe client)  │ spawn│  (winlogon token,        │
│  ├── PipeServer (main pipe)     │ via  │   System integrity)      │
│  ├── InputInjectionAgent (CAD)  │ CAPU │                          │
│  ├── UnattendedSessionManager   │      │  All input injection,    │
│  ├── ServiceConfig              │      │  desktop polling 500ms,  │
│  └── ServiceLogger              │      │  pipe server.            │
│                                 │      └──────────────────────────┘
└─────────────────────────────────┘
```

Legend: CAPU = CreateProcessAsUser.

## Process spawning — how it actually works

### UI helper (`ZConnect.exe`) via `SessionMonitor`

1. `SessionMonitor.Tick()` вызывается каждую 1s из `ServiceWorker.ExecuteAsync` loop
2. `WTSGetActiveConsoleSessionId()` → обнаруживает активный user session
3. Если UI helper не запущен или exited → `SpawnHelper(sessionId)`
4. `WTSQueryUserToken(sessionId, out token)` — получает **user token** (не winlogon!)
5. `CreateEnvironmentBlock(token)` — user environment
6. `CreateProcessAsUser(token, "ZConnect.exe", ... lpDesktop="WinSta0\\Default")`
7. Child process наследует user SID, profile, %APPDATA%, DPAPI CurrentUser keys

Почему user token, не winlogon:
- Старые версии (до Stage 1) использовали winlogon token (SYSTEM-in-session-N). Это давало elevation, но ломало DPAPI CurrentUser + %APPDATA% пути
- Сейчас UiApp manifest — `asInvoker`, не `requireAdministrator`. User token работает без elevation
- UAC-требующие операции (HKLM write) теперь delegate'ся в service через pipe

### InputHelper (`ZConectInputHelper.exe`) via `InputHelperManager`

Иное: нужен **winlogon token** (System integrity) чтобы bypass UIPI barrier на UAC/secure desktop.

1. `InputHelperManager.ManageLoopAsync` работает фоновым task'ом
2. Если helper process not running / crashed → `SpawnHelperAsync`
3. `FindWinlogonPid(sessionId)` — находит `winlogon.exe` в user session
4. `OpenProcess(PROCESS_QUERY_INFORMATION, winlogonPid)` → process handle
5. `OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_QUERY, out procToken)`
6. `DuplicateTokenEx(procToken, TOKEN_ALL_ACCESS, SecurityImpersonation, TokenPrimary, out dupToken)`
7. `CreateEnvironmentBlock(dupToken)` — winlogon environment
8. `CreateProcessAsUser(dupToken, "ZConectInputHelper.exe --pipe-name ZConect_Input_Helper_IPC")` on `WinSta0\Default`
9. Service connects to helper pipe as client (`NamedPipeClientStream`)

### Respawn logic

| Child | Detection | Delay |
|---|---|---|
| UI (`ZConnect.exe`) | `SessionMonitor.Tick` каждую 1s checks `Process.HasExited` | 3s after crash |
| InputHelper | `ManageLoopAsync` checks `_helperProcess?.HasExited` каждую итерацию | 5s after spawn fail, 1s after pipe connect fail |

Suppression: `UserExitRequested` flag (устанавливается когда UI шлёт `user_exit` через pipe) — SessionMonitor не будет respawn UI пока user manually не закроет → не переткнёт.

## Installation

```powershell
# Build
dotnet build client/ZConectService/ZConectService.csproj -c Release -r win-x64

# Install (требует admin)
ZConectService.exe --install

# Start
sc start ZConectService

# Check status
sc query ZConectService

# View logs
type C:\ProgramData\ZConect\logs\service.log

# Stop
sc stop ZConectService

# Uninstall
ZConectService.exe --uninstall
```

В production использовать InnoSetup installer (`installer/build.ps1`) — сам stop + uninstall старый service, copy new binaries, register, start.

## CLI Commands

| Command | Description |
|---|---|
| `ZConectService.exe` | Run as Windows Service (called by SCM) |
| `ZConectService.exe --install` | Register as auto-start service (requires admin) |
| `ZConectService.exe --uninstall` | Stop service, wait for STOPPED, unregister from SCM |
| `ZConectService.exe --start` | Start service via SCM |
| `ZConectService.exe --stop` | Stop service, **wait** for SERVICE_STOPPED (polls `sc query`, 15s timeout — `StopAndWait`). Используется installer'ом для безопасного overwrite. |
| `ZConectService.exe --help` | Show usage info |

## Configuration

**Path:** `C:\ProgramData\ZConect\service-config.json`

```json
{
  "MachineId": "auto-generated-guid",
  "SignalingUrl": "http://server:8080",
  "WebSocketUrl": "ws://server:8080/ws",
  "StunUrl": "stun:server:3478",
  "TurnUrl": "turn:server:3478",
  "TurnUsername": "zconect",
  "TurnPassword": "encrypted-via-dpapi",
  "DeviceSecret": "auto-saved-encrypted-via-dpapi",
  "UnattendedPassword": "encrypted-via-dpapi",
  "UnattendedEnabled": true,
  "HelperExePath": "",
  "AutoSpawnHelper": true
}
```

| Field | Description |
|---|---|
| `MachineId` | GUID, auto-generated на first run |
| `DeviceSecret` | DPAPI-encrypted device binding, auto-saved после первой create session |
| `HelperExePath` | Override путь к ZConnect.exe (empty = auto-detect — см. Helper Discovery) |
| `AutoSpawnHelper` | Whether to auto-launch UI helper |
| `UnattendedEnabled` | Persistent session через machine_id (default true) |
| `TurnPassword` / `UnattendedPassword` | DPAPI-encrypted secrets |

**DPAPI scope**: **LocalMachine** — читается только SYSTEM / admins на этой машине. НЕ переносится на другие машины (то есть если украсть service-config.json, secrets не расшифруешь).

**UI settings** (`%AppData%\ZConect\client-settings.json`) используют **CurrentUser** scope. DpapiHelper.Decrypt теперь fallback'ает на LocalMachine при fail — self-migration старых configs.

## Named Pipe IPC — двухтрубная архитектура

Service имеет **два** pipe'а:

### Main pipe (`ZConect_Service_IPC`) — server

- Service = `NamedPipeServerStream`, UI = client
- **ACL**: `InteractiveSid` (ReadWrite) + `LocalSystemSid` (FullControl)
- **Post-connect SID verification**: после первого `ReadAsync` service делает `RunAsClient` → проверяет что caller's SID = current active console user SID (via `WtsActiveUserInfo`). Отклоняет если mismatch → предотвращает другого interactive user (logged off / other account) подключиться к pipe.
- **`_writeLock` (SemaphoreSlim)** — сериализует header+payload writes (60 Hz mouse move был interleave'ить без lock'а)

**Messages**:

| Type | Direction | Purpose |
|---|---|---|
| `hello` | UI → Service | Handshake при ConnectAsync, передаёт pid + sessionId |
| `config` | Service → UI | Signaling/TURN/STUN URLs, credentials, session codes (login/pass/owner_secret), device_secret, machine_id |
| `status` | UI → Service | Reports viewer connection status |
| `user_exit` | UI → Service | User manually closed GUI — не respawn'ить UI |
| `set_uac_policy` | UI → Service | Toggle `PromptOnSecureDesktop` (0/1); HKLM write требует SYSTEM |
| `update_config` | UI → Service | Update TURN/STUN URLs + credentials; service saves + re-encrypts DPAPI |
| `inject_mouse` | UI → Service | Forward to InputHelper via helper pipe |
| `inject_keyboard` | UI → Service | Forward to InputHelper |
| `inject_ctrl_alt_del` | UI → Service | Dispatch to `InputInjectionAgent` (SAS в session 0) |
| `desktop_changed` | Service → UI | Relay из helper: active input desktop switched (Default ↔ Winlogon) |

### Helper pipe (`ZConect_Input_Helper_IPC`) — client

- Service = `NamedPipeClientStream`, Helper = server (inverted — helper spawns first)
- **ACL**: `LocalSystemSid` only (helper restricts pipe)
- **`_writeLock`** аналогично main pipe

**Messages**:

| Type | Direction | Purpose |
|---|---|---|
| `inject_mouse` | Service → Helper | Forward from UI; helper `SetThreadDesktop` + `SetCursorPos` + `SendInput` |
| `inject_keyboard` | Service → Helper | Same для keyboard |
| `desktop_changed` | Helper → Service | Helper's poll loop detected desktop switch; service relays в UI via main pipe |

**Wire format** (оба pipe'а): `[4 bytes LE length] [UTF-8 JSON payload]`. Max payload 1 MB (cap против DoS).

**Message routing diagram:**
```
UI → main pipe → Service → helper pipe → Helper → SendInput
Helper → helper pipe → Service → main pipe → UI
```

Service не обрабатывает `inject_*` сам (кроме CAD) — только forward'ит. CAD — единственное исключение (SendSAS требует session 0).

## Logging

**Path:** `C:\ProgramData\ZConect\logs\service.log`

**Format:** JSON Lines, same схема как UI `logs.log`.

```json
{"ts":"2026-04-18T13:01:45.23Z","level":"INFO","module":"Service","event_name":"service_started","error":null}
{"ts":"2026-04-18T13:01:45.87Z","level":"INFO","module":"InputHelper","event_name":"helper_spawned pid=25732 session=4","error":null}
```

**Rotation:** 5 MB max, 3 backup files (`service.log.1`, `.2`, `.3`).

**Modules:**
- `Service` — lifecycle
- `SessionMonitor` — UI helper spawn/exit
- `InputHelper` — InputHelper spawn, pipe connect, desktop_changed relay
- `Pipe` — main pipe events (SID verify, hello, config send, relay dispatches)
- `Unattended` — session create/refresh/stop
- `InputAgent` — CAD flow (SendSAS results)

**Key events:**

| Event | Level | Meaning |
|---|---|---|
| `service_started` | INFO | Service started |
| `helper_spawned pid=X session=Y` (Module: SessionMonitor) | INFO | UI helper launched |
| `helper_spawned pid=X session=Y` (Module: InputHelper) | INFO | **InputHelper** process launched (different module) |
| `pipe_connected_to_helper` | INFO | Service's helper-pipe client connected |
| `helper_connected` | INFO | UI connected к main pipe |
| `helper_connected_sid_verified` | INFO | SID check passed |
| `pipe_sid_mismatch` | WARN | Rejected — caller SID ≠ active console user |
| `config_sent login=****XXXX` | INFO | Config delivered с masked codes |
| `desktop_changed_from_helper to=X` | INFO | Relay через pipe chain |
| `turn_password_not_configured_stun_only` | WARN | TURN auth будет fail — set TurnPassword |
| `uac_secure_desktop_set_to_X_via_service` | INFO | Toggle HKLM succeeded |
| `helper_disconnected` | INFO | UI pipe closed normally |
| `helper_pipe_disconnected` | WARN | Helper pipe closed (ManageLoop will reconnect) |
| `config_push_pipe_disposed_race` | DEBUG | Expected disconnect race в PushConfigToHelperAsync |
| `CreateProcessAsUser_failed` | ERROR | UI spawn failed |

## Unattended Access

When `UnattendedEnabled = true` (default), service автоматически создаёт и поддерживает сигналинг session:

1. **Startup**: `UnattendedSessionManager.StartSessionAsync` → `POST /api/v1/session/create` с `machine_id` + `device_secret`
2. **Server**: если `machine_id` известен → возвращает existing session (те же коды); иначе создаёт новую + generates `device_secret` → service сохраняет encrypted
3. **Every 2 min** `TickAsync` → `POST /api/v1/session/refresh` с `regenerate_pass:false` → TTL extended, codes не меняются
4. **Reboot**: same `machine_id` + `device_secret` → server возвращает same login/pass
5. **Service stop**: session НЕ закрывается (stays alive 24h TTL) — при быстром restart тот же код
6. **UI handshake**: UI connects to pipe → service sends `config` с codes; UI показывает ТЕ ЖЕ codes что на других machine'ах этой же session

**Key events (Unattended module):**

| Event | Level | Meaning |
|---|---|---|
| `creating_session` | INFO | StartSessionAsync in progress |
| `session_created id=X login=****Y pass=****Z` | INFO | Ready |
| `has_device_secret=True/False` | INFO | Startup state |
| `device_secret_saved` | INFO | Received from server на first create |
| `session_refreshed login=****Y` | DEBUG | TTL extended |
| `session_refresh_failed_will_recreate` | WARN | Fell back на recreate |

## Helper Discovery

Search order для `ZConnect.exe`:

1. `HelperExePath` из config (если не пусто + file exists)
2. Same directory как `ZConectService.exe`
3. Parent directory (dev mode)
4. Sibling `UiApp` project: walks up from service binary, checks `UiApp/bin/{Debug,Release}/net8.0-windows/win-x64/ZConnect.exe` — для locale dev run без installer'а

For InputHelper: hardcoded `Path.Combine(Path.GetDirectoryName(helperPath)!, "ZConectInputHelper.exe")` — то есть рядом с UI.

## Crash Recovery

**Service itself** (SCM policy):
- 1st failure: restart after 5s
- 2nd failure: restart after 10s
- 3rd failure: restart after 30s
- Counter resets после 24h

**UI helper** crash:
- Detected каждую 1s через `Process.HasExited` в `SessionMonitor.Tick`
- Respawns after 3s delay
- `ShouldSuppressRespawn` callback (set to `_pipeServer.UserExitRequested`) — user exit prevents respawn
- Exit code логируется для диагностики

**InputHelper** crash:
- Detected в `ManageLoopAsync` каждую итерацию
- Respawns after 5s (spawn fail) / 1s (connect fail) delays
- `_helperProcess` snapshot под `_processLock` для safe concurrent access

## Technologies

- **.NET 8** Worker Service (`Microsoft.Extensions.Hosting.WindowsServices`)
- **Win32 P/Invoke**: `WTSGetActiveConsoleSessionId`, `WTSQueryUserToken`, `OpenProcess`, `OpenProcessToken`, `DuplicateTokenEx`, `CreateProcessAsUser`, `CreateEnvironmentBlock`, `RunAsClient` (pipe impersonation)
- **DPAPI**: `System.Security.Cryptography.ProtectedData` (LocalMachine scope)
- **SCM**: `ServiceController` API для start/stop; CLI via `sc.exe`
- **Named pipes**: `NamedPipeServerStreamAcl.Create` с PipeSecurity

## Tests

420 unit tests (non-Live) covering service code:
- `ServiceLoggerTests` — file creation, JSON format, levels, rotation, thread safety
- `ServiceConfigTests` — defaults, serialization, network fields
- `ServiceInstallerTests` — constants verification
- `PipeProtocolTests` — wire format, message serialization, all message types
- `PipeProtocolInjectTests` — camelCase round-trips для inject_* messages
- `PipeRaceTests` (new 2026-04-18) — concurrent sends, disconnect races, reconnect, TOCTOU scenarios
- `ServiceLifecycleTests` — user_exit flag, respawn suppression
- `ServiceIntegrationTests` — full hello/config/status handshake
- `GuiServiceIntegrationTests` — UI ↔ service end-to-end

1 LiveHost Fact с 9 scenarios validates end-to-end (включая `Winlogon_ctrl_alt_del_recovery` — реальный SendSAS test).
