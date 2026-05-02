# Архитектура ZConnect

Последнее обновление: 2026-04-22 (после admin panel + brute-force protection + L10n).

## 1) Общее

Система состоит из:

- **Windows-клиента** — три процесса: Service (SYSTEM, Session 0), UI (user token, user session), InputHelper (winlogon token, user session). Подробнее в §3.
- **Сервера сигналинга** — Go 1.25 на Ubuntu 24 LTS в Docker (образ Alpine 3.21).
- **STUN/TURN сервера** — coturn 4.6.2 для прохождения NAT.
- **Telemetry dashboard** — SQLite, встроен в signaling сервер.

Передача видео: VP8 (WebRTC software encoding via `Microsoft.MixedReality.WebRTC 2.0.2` — **заархивирован Microsoft, в TODO миграция на SIPSorcery**).
Транспорт: WebRTC (P2P при возможности, TURN relay при необходимости).
Захват экрана: DXGI Output Duplication (primary) с GDI fallback в `MixedRealityPeerConnectionAgent` когда DXGI возвращает `AccessLost` (desktop switch) или timeout 5s.
Сетевой стек: IPv4.

## 2) Ключевые сценарии

### 2.1 Создание сессии (host)

1. Клиент A нажимает **Создать сессию** → становится `host`.
2. Сервер создаёт сессию, генерирует `login_code` (8 цифр) + `pass_code` (8 цифр).
3. При `UnattendedEnabled` service создаёт сессию сам при загрузке Windows, коды сохраняются в `service-config.json` (DPAPI LocalMachine).
4. Клиент A показывает коды пользователю.
5. При подключении viewer'а — host показывает диалог подтверждения (если `RequireConfirmation=true`).

### 2.2 Подключение (viewer)

1. Клиент B вводит `login_code` + `pass_code`.
2. Сервер валидирует коды + TTL + progressive tier lockout (см. §4.2).
3. Сервер связывает A и B через signaling WebSocket.
4. Клиенты выполняют SDP/ICE обмен. ICE candidates: `host` (LAN) → `srflx` (STUN) → `relay` (TURN).
5. Устанавливается медиа-канал (VP8) + data channels (Control/Input/Clipboard/File).

При server reject'е client показывает локализованное сообщение:
- 423 SessionBlocked → MessageBox «Сессия заблокирована, host должен обновить пароль»
- 429 SessionLocked + Retry-After → status bar countdown «Попробуйте через N сек»
- 403 Banned → MessageBox «Ваш IP заблокирован»
Детали: [API.md §3.1 + §5](API.md#31-session-brute-force-protection-per-session-not-per-ip).

### 2.3 Unattended mode

1. Windows Service при старте создаёт сессию с `machine_id` + `device_secret` (заpersisted DPAPI LocalMachine).
2. Сессия переживает перезагрузки машины — device_secret identifies as same machine.
3. UI получает коды через Named Pipe IPC от Service на `hello` handshake.
4. Service автоматически spawn'ит UI (`ZConnect.exe`) в активной пользовательской сессии через `CreateProcessAsUser` + WTSQueryUserToken.

### 2.4 UAC elevation (secure desktop)

1. Пользователь на host'е кликает что-то требующее UAC → Windows переключает active input desktop на `Winlogon`.
2. Service's InputHelperManager получает `desktop_changed=Winlogon` от InputHelper (poll every 500ms).
3. Service пересылает event в UI через main pipe.
4. UI обновляет `_cachedActiveDesktop` и форсирует DXGI capture reinit (tigervnc pattern — re-create duplication on new desktop).
5. Viewer видит UAC dialog. Клики viewer'а → UI → main pipe → service → helper pipe → InputHelper → `SetThreadDesktop(winlogon_desktop)` + `SendInput`.

### 2.5 Ctrl+Alt+Del

1. Viewer нажимает CAD button в toolbar.
2. UI шлёт `inject_ctrl_alt_del` через main pipe.
3. Service's `InputInjectionAgent` (thread-worker в Session 0) вызывает `SendSAS(AsUser=false)`. SendSAS требует session 0 service context — **только это место не проходит через helper**.
4. Windows переключает на Winlogon desktop. На Win11 работает корректно (см. memory `project_todo_cad_driver.md`).

## 3) Клиентская архитектура — three-process

```
┌──────────────────────────────────────────────────────────┐
│ ZConectService.exe                                       │
│ SYSTEM, Session 0                                        │
│                                                          │
│ Владеет: service-config.json, HKLM registry              │
│                                                          │
│ Процессы, которые spawn'ит:                              │
│   ├─ UI helper (ZConnect.exe) — user token              │
│   └─ InputHelper (ZConectInputHelper.exe) — winlogon token│
└──────┬─────────────────────────────────┬─────────────────┘
       │ Main pipe                       │ Helper pipe
       │ ZConect_Service_IPC             │ ZConect_Input_Helper_IPC
       │ (ACL: InteractiveSid +          │ (ACL: LocalSystemSid only)
       │  post-connect SID verify)       │
       ▼                                 ▼
┌──────────────────────┐       ┌──────────────────────────┐
│ ZConnect.exe (UI)    │       │ ZConectInputHelper.exe   │
│ USER TOKEN           │       │ WINLOGON TOKEN           │
│ Medium integrity     │       │ System integrity         │
│ Session N WinSta0    │       │ Session N WinSta0        │
│                      │       │                          │
│ WebRTC + DataChannels│       │ Все input injection      │
│ WPF UI, settings     │       │ (mouse/kbd/desktop switch)│
│ DPAPI CurrentUser    │       │ Active desktop polling   │
│ %APPDATA%\ZConect\   │       │ PER_MONITOR_AWARE_V2     │
└──────────────────────┘       └──────────────────────────┘
```

### 3.1 Почему именно три процесса

Четыре фундаментальных ограничения Windows делают single-process невозможным:

1. **HKLM registry write** → требует SYSTEM token → только service.
2. **Session 0 isolation** → process в session 0 не видит desktop активной user session (SetCursorPos/SendInput не долетают).
3. **Window station scope** → SetThreadDesktop требует thread's window station = target desktop's window station. Impersonation меняет token, НЕ меняет winstation. Единственный способ быть в user's WinSta0 — жить там отдельным процессом.
4. **UIPI (User Interface Privilege Isolation)** → Medium-integrity process (UI под user token) не может SendInput в System-integrity окна (UAC, Task Manager, Registry Editor). Нужен System integrity → winlogon token helper.
5. **User profile access** (DPAPI CurrentUser, %APPDATA%) требует user token — только UI работает корректно с ними.

Ни один single-process дизайн не закрывает все пять. Three-process — минимально работающее решение.

### 3.2 Pipe протоколы

**Wire format:** `[4 bytes LE length][UTF-8 JSON payload]`. Единый `PipeProtocol` class используется на обеих сторонах.

**Main pipe** (`ZConect_Service_IPC`):
- Server: Service, Client: UI
- ACL: `InteractiveSid` (interactive users) + `LocalSystemSid` (service себя)
- SID verification **после** первого `ReadAsync` (RunAsClient требует impersonation context, который есть только после чтения первого сообщения)
- UI→Service: `hello`, `status`, `user_exit`, `update_config`, `set_uac_policy`, `inject_mouse`, `inject_keyboard`, `inject_ctrl_alt_del`
- Service→UI: `config` (signaling/TURN/session codes), `desktop_changed` (relay от helper)
- `_writeLock` (SemaphoreSlim) в клиенте и сервере — сериализация header+payload writes, предотвращает interleaving 60Hz mouse move

**Helper pipe** (`ZConect_Input_Helper_IPC`):
- Server: InputHelper, Client: Service
- ACL: `LocalSystemSid` только — защита от unprivileged process hijack
- Service→Helper: `inject_mouse`, `inject_keyboard`
- Helper→Service: `desktop_changed`

**Message routing:**
```
UI → main pipe → Service (relay only) → helper pipe → Helper → SendInput
Helper → helper pipe → Service (relay only) → main pipe → UI
```

Service не обрабатывает `inject_*` сам (кроме CAD), только forward'ит. Единственное исключение — `inject_ctrl_alt_del` идёт в `InputInjectionAgent` (SendSAS требует session 0).

### 3.3 Структура проектов

| Проект | Назначение |
|---|---|
| `client/UiApp` | WPF GUI, ViewModels, Services, Dialogs |
| `client/ZConectService` | Windows Service (SYSTEM), main pipe server, helper management |
| `client/ZConectInputHelper` | Helper process (winlogon token), ~470 LOC Program.cs |
| `client/SessionClient` | HTTP API клиент (create/join/close/refresh) |
| `client/WebRtcTransport` | WebRTC agent, signaling coordinator, WS client |
| `client/ScreenCapture` | DXGI/GDI захват, display enumeration |
| `client/QualityController` | Auto-quality adapter, preset ladder |
| `client/FileTransfer` | Файловый трансфер по data channel |
| `client/ZConect.Tests` | 420 unit + 1 LiveHost Fact (9 scenarios) |

### 3.4 Routing решения в UI

После коммита `0c7c6ec` (2026-04-18) **ВСЕ** mouse/keyboard клики идут через helper когда service pipe connected:

```csharp
if (_servicePipe is not null && _servicePipe.IsConnected)
    _ = _servicePipe.SendInjectMouseAsync(...)     // → main pipe → helper
else
    _windowsInputInjectionService.InjectMouse(...) // fallback standalone
```

Fallback direct injection — только когда service pipe недоступен (standalone mode). В этом режиме UAC/secure desktop clicks не работают (UIPI блок), но обычные клики проходят.

### 3.5 Компоненты service

| Компонент | Роль |
|---|---|
| `PipeServer` | Main pipe server, SID verify, config dispatcher |
| `InputHelperManager` | Spawn + pipe client для helper, respawn loop |
| `SessionMonitor` | Spawn + respawn UI helper (`ZConnect.exe`) |
| `InputInjectionAgent` | CAD / SAS worker thread в session 0 |
| `UnattendedSessionManager` | Persistent session создание, refresh, device_secret |
| `ServiceConfig` | DPAPI LocalMachine secrets, machine_id |
| `ServiceLogger` | JSONL + 5 MB rotation |

**Ранее существовавшие и удалённые:** `ServiceDesktopMonitor` — дублировал InputHelper polling через WinlogonImpersonation, удалён 2026-04-18. `WinlogonImpersonation` оставлен — используется только в `InputInjectionAgent` для CAD.

### 3.6 Модель потоков (UI side)

- **UI поток**: WPF Dispatcher, PropertyChanged уведомления.
- **Capture поток**: выделенный `Thread` с high-precision loop (`Stopwatch` + `SpinWait`), DXGI/GDI + cursor compositing.
- **Encode**: WebRTC internal thread (VP8 from `ExternalVideoTrackSource`).
- **Network**: signaling WebSocket + WebRTC callbacks.
- **Pipe read**: background Task в ServicePipeClient, auto-reconnect с exponential backoff (1s→60s cap).
- **File transfer**: chunks через data channel с SCTP backpressure.
- **Telemetry**: heartbeat каждые 5 мин.

### 3.7 MainViewModel partial-файлы

| Файл | Ответственность |
|------|----------------|
| `MainViewModel.cs` | поля, конструктор, settings, service integration |
| `MainViewModel.Connection.cs` | lifecycle сессии, ConnectWsAsync, WS reconnect, OnServiceDesktopChanged |
| `MainViewModel.Ice.cs` | ICE мониторинг, route inference, UI-индикация |
| `MainViewModel.Video.cs` | захват/рендер, display switching, auto-quality, RTT |
| `MainViewModel.Clipboard.cs` | clipboard sync loop |
| `MainViewModel.FileTransfer.cs` | отправка/приём файлов |
| `MainViewModel.Input.cs` | viewer input → routing (helper vs direct) |
| `MainViewModel.Uac.cs` | UAC Secure Desktop policy via service pipe |
| `MainViewModel.AddressBook.cs` | адресная книга |

### 3.8 Персистенция

| Файл | Владелец | Scope DPAPI |
|---|---|---|
| `C:\ProgramData\ZConect\service-config.json` | Service (SYSTEM) | LocalMachine (TurnPassword, DeviceSecret, UnattendedPassword) |
| `%AppData%\ZConect\client-settings.json` | UI (user) | CurrentUser (LastPassCode, LastLoginCode, TurnPassword). Atomic tempfile+rename write. Legacy LocalMachine scope auto-migrated on first decrypt |
| `%AppData%\ZConect\address-book.json` | UI (user) | Plaintext (без секретов) |
| `C:\ProgramData\ZConect\logs\*.log` | Service + InputHelper (SYSTEM) | — (JSONL, rotation 5 MB × 3) |
| `%LocalAppData%\ZConect\logs\*.log` | UI (user) | — |

## 4) Серверная архитектура

### 4.1 Структура пакетов (`server/`)

| Пакет | Назначение |
|---|---|
| `cmd/signaling` | Entry point, wiring adapters |
| `cmd/admin-hashpass` | CLI для bcrypt hash admin password |
| `cmd/admin-verify` | CLI debug helper для verify bcrypt match |
| `internal/api` | HTTP handlers (create/join/close/refresh/healthz/presence) |
| `internal/signaling` | WebSocket handler + Hub (peer relay) |
| `internal/session` | Session store (in-memory), states, progressive lockout |
| `internal/auth` | HMAC-SHA256 token service (60s TTL) |
| `internal/ratelimit` | Per-IP sliding-window rate limiter |
| `internal/config` | Env-based configuration |
| `internal/logging` | JSONL logger with rotation (5 MB, 3 backups) |
| `internal/telemetry` | SQLite store, GeoIP, dashboard, heartbeat handler |
| `internal/admin` | Session-based admin panel — auth, audit log (NDJSON), 10 UI tabs, IP ban list (с TTL), CSRF, password rotation |
| `internal/bruteforce` | Per-IP enumeration tracker (sliding window) для "Подозрительные IP" tab — observability, не autoban |
| `internal/update` | Auto-update endpoint /api/v1/client/version (JSON with SHA256) |

### 4.2 WebSocket auth flow

1. Client → `/ws?session_id=XXX` (token НЕ в URL)
2. Client шлёт: `{"type":"auth","session_id":"XXX","payload":{"token":"hmac"}}`
3. Server валидирует HMAC token (60s TTL) в пределах 5s deadline
4. Valid → peer added to hub; invalid → connection closed

### 4.3 Peer relay

- Max 3 peers per session (перекрытие при VPN reconnect)
- Stale peers auto-cleanup при failure read-loop
- Per-peer write deadline 5s (slow-receiver DoS protection)
- Per-peer rate limit: 30 msg/sec, 60 burst
- Reserved message types блокируются от relay: `peer_disconnected`, `error`, `system_error`, `system_notice`, `auth`

### 4.4 Security

- HTTP body limit: 4 KB на всех endpoints
- Expired/closed sessions отбрасываются до WS upgrade
- **Progressive per-session lockout**: 5 wrong pass → tier 1 (15s) → 5 more →
  tier 2 (2m) → tier 3 (15m) → tier 4 (1h) → tier 5+ = StateBlocked permanent.
  Host разблокирует через `POST /session/refresh?regenerate_pass=true`.
  Per-session (не per-IP) чтобы не ломать NAT users. Детали: [API.md §3.1](API.md#31-session-brute-force-protection-per-session-not-per-ip).
- **Per-IP enumeration tracker** (`internal/bruteforce`): observability для attacker'ов
  с несуществующими login_codes (session-level protection на них не срабатывает).
  Sliding 5-min window, exposed через `/admin/api/suspicious-ips`.
  Automatic ban **не делается** — admin manual decision.
- **Admin IP ban list** (`admin.BanEntry`): manual через UI tab «IP блокировки».
  Infrastructure для TTL exists (ExpiresAt + AutoBanIP helper + lazy cleanup)
  но wired только для permanent bans пока.
- Rate limit: `RATE_LIMIT_PER_MIN` (default 200) per-IP
- Admin panel: bcrypt cost 12 + session cookie + CSRF + IP whitelist (env
  `ADMIN_ALLOWED_IPS`) + persistent password file
- Secrets masked в client logs
- Origin check: deny WS без Origin в production если `ALLOWED_ORIGINS` не пуст

### 4.5 Telemetry

- Client heartbeat каждые 5 мин: `machine_id`, OS, language, app version
- Country via ip-api.com (24h cache)
- SQLite DB в Docker volume
- Dashboard: `/dashboard?key=XXX` с auto-refresh

## 5) Data channels

| Channel | Purpose | Protocol |
|---|---|---|
| `dc-control` | Screen meta, displays, cursor, folder ops, ping/pong | JSON |
| `dc-input` | Mouse + keyboard events | JSON |
| `dc-clipboard` | Clipboard text sync (max 256 KB) | JSON |
| `dc-file` | File transfer (meta, chunks, ack, error) | JSON + binary chunks |

## 6) Видео и качество

### 6.1 Профили

| Профиль | Разрешение | FPS | Битрейт |
|---------|-----------|-----|---------|
| Extra Low | 640×360 | 20 | 400 kbps |
| Low | 854×480 | 20 | 900 kbps |
| Medium | 1280×720 | 30 | 2200 kbps |
| High | 1920×1080 | 30 | 4200 kbps |
| Auto | adaptive | adaptive | adaptive |

### 6.2 Захват

- **DXGI Output Duplication** — GPU-capture, dirty rects (~30-50% CPU saving)
- **Multi-Output Stitching** — "All" мониторов, independent D3D11 device per output
- **Double buffering** — pinned buffers, zero-copy to WebRTC encoder
- **High-precision loop** — Thread + Stopwatch + SpinWait (~1ms)
- **Frame skip statistics** — `ConsumeFrameSkipStats` tracks skipped vs total frames. Реальный adaptive FPS не реализован — работает на constant rate profile, throttling только через AutoQuality (смена preset → новый target FPS).
- **Remote cursor rendering** — host не композитит курсор в video. `CursorShapeService` детектит тип курсора через `GetCursorInfo` + `GetIconInfoExW` → `wResID` (resource ID, desktop-independent) → строковое имя (`"arrow"`, `"ibeam"`, ...) → через `dc-control`. Viewer рендерит курсор локально своими ассетами поверх видео — lossless, без compression artefact'ов.
- **GDI fallback** — когда DXGI теряет access (UAC desktop switches); реализован в `MixedRealityPeerConnectionAgent`

### 6.3 Auto-quality

- Loop 1.5s, ladder Extra Low ↔ Low ↔ Medium ↔ High
- Degrade: dropRatio > 20% ИЛИ bitrate < 50% target; streak 5 samples
- Improve: dropRatio < 3% И bitrate ≥ 85% target; streak 12 samples
- Cooldown: 8 samples после change

## 7) Тесты

- **420 unit тестов** (non-Live): SafePath, IceUpgrade, WsReconnection, CursorShape, AddressBook, FileTransfer, PipeProtocol, **PipeRace** (новый, 7 тестов для concurrent send / disconnect / reconnect TOCTOU), ServiceLifecycle, BootIntegration, LogAudit, UacDesktop, UacIntegration, GuiServiceIntegration и др.
- **1 LiveHost Fact** (9 scenarios): Video_sustained_fps, FT_large_file_5mb, FT_path_traversal_blocked, FT_dir_list, RTT_ping_pong_under_500ms, FT_gzip_compressed_round_trip, Quality_switch_changes_frame_size, Video_max_frame_gap_under_10s, Winlogon_ctrl_alt_del_recovery.
- **~30 Live server tests** против `YOUR_SERVER_IP`: API CRUD, WS auth, relay, peer disconnect notification, reserved types, peer limit, session expiry, body limit, join lockout, method validation.

## 8) Security audit 2026-04-18 (round 1+2)

11 fix'ов, все под test protection:

| Severity | Fix | Файл |
|---|---|---|
| CRITICAL | C6 — ServicePipeClient.SendAsync TOCTOU | `UiApp/Services/ServicePipeClient.cs` |
| CRITICAL | C1 — InputHelperManager.SendToHelperAsync TOCTOU | `ZConectService/InputHelperManager.cs` |
| CRITICAL | C2 — `_helperProcess` lock | `ZConectService/InputHelperManager.cs` |
| CRITICAL | C3 — PipeServer push methods ObjectDisposed discipline | `ZConectService/PipeServer.cs` |
| CRITICAL | C4 — InputHelper JsonDocument native-pool leak | `ZConectInputHelper/Program.cs` |
| CRITICAL | C5 — `_currentDesktop` volatile | `ZConectInputHelper/Program.cs` |
| HIGH | C7 — `_cachedActiveDesktop` volatile | `UiApp/ViewModels/MainViewModel.cs` |
| HIGH | C9 — `ConnectHelperPipeAsync` pipe leak on fail | `ZConectService/InputHelperManager.cs` |
| HIGH | Settings.Save atomic write | `UiApp/Services/SettingsService.cs` |
| HIGH | DPAPI LocalMachine fallback (auto-migration) | `UiApp/Services/DpapiHelper.cs` |
| HIGH | UAC dead code removed, toggle via pipe | `UiApp/ViewModels/MainViewModel.Uac.cs` |

Detailed in `SECURITY_AUDIT_2026-04-10.md` addendum section.

## 9) Известные ограничения

- **Signaling over HTTP, не HTTPS** — session codes в plaintext на wire. Critical TODO для beta (см. `ROADMAP.md`).
- **Только IPv4**.
- **Win+L lock screen clicks не работают** — Windows kernel фильтрует synthetic input на secure desktop независимо от integrity level. Требует WHQL-signed kernel driver (как у TeamViewer/AnyDesk).
- **DXGI cascading failure** — иногда после первого winlogon_dxgi_failed_gdi DXGI на Default тоже ломается с E_ACCESSDENIED. Simple `GC.Collect` fix в TODO.
- **`Microsoft.MixedReality.WebRTC` archived** Microsoft'ом, `mrwebrtc.dll` 2.0.2 из 2020 без security fix'ов. TODO: миграция на SIPSorcery.
- **HW encoding (H.264/HEVC)** — отложено, не работает на VM.
- **Single signaling server** — SPOF для production.
