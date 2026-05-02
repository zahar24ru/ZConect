# Статус реализации

Последнее обновление: 2026-04-28. Текущая версия: **1.0.1** (release `5ac4e9e`).

Документ отражает **текущее состояние** (не историю). История — в `git log`.

## ✅ Сделано

### Core remote desktop

- **WebRTC P2P** — VP8 видео + 4 data channels (`dc-control`, `dc-input`, `dc-clipboard`, `dc-file`) через `Microsoft.MixedReality.WebRTC 2.0.2`
- **DXGI capture** — Output Duplication, GPU-захват с dirty rects (~30-50% CPU saving)
- **Multi-Output stitching** — "All" мониторов через independent D3D11 device per output
- **GDI fallback** — в `MixedRealityPeerConnectionAgent` когда DXGI возвращает `AccessLost` / timeout 5s
- **Remote cursor rendering** — тип курсора детектится через `GetIconInfoExW.wResID`, посылается строкой через dc-control, viewer рендерит локально своими ассетами
- **Remote input** — mouse/keyboard через `SendInput`, координаты мапятся по `screen_meta`

### Three-process architecture (Stages 1-3, завершено 2026-04-18)

- **ZConectService.exe** — SYSTEM в Session 0, HKLM registry write, main pipe server
- **ZConnect.exe (UI)** — user token в user session, WebRTC + WPF, service пересоздаёт через `WTSQueryUserToken + CreateProcessAsUser`
- **ZConectInputHelper.exe** — winlogon token (SYSTEM-in-session-N), все input injection + desktop polling; service spawn'ит через `OpenProcess(winlogon.exe) → DuplicateTokenEx`
- **Two pipes**: main `ZConect_Service_IPC` (ACL: InteractiveSid + SID verify post-first-read), helper `ZConect_Input_Helper_IPC` (ACL: LocalSystemSid only)
- **UAC secure desktop clicks работают** — helper под System integrity bypass'ит UIPI
- **Win11 Ctrl+Alt+Del работает** — `SendSAS(asUser=false)` из session 0 InputInjectionAgent

### Session management

- **Unattended mode** — service создаёт сессию при загрузке Windows, коды пересоздаются каждый `SessionTtl` через refresh
- **Persistent session** — `machine_id` + `device_secret` (DPAPI LocalMachine), выживают перезагрузки
- **Viewer confirmation gate** — host диалог подтверждения перед акцептом viewer'а (RequireConfirmation по умолчанию true)
- **Address book** — локальный список контактов в `%AppData%\ZConect\address-book.json`
- **Auto-close session on exit** — POST /session/close при штатном закрытии UI

### Quality & performance

- **Auto-quality ladder** — Extra Low ↔ Low ↔ Medium ↔ High с hysteresis (streak 5 degrade, 12 improve, 8 cooldown, 20% frame drop threshold)
- **Bitrate hints** — SetBitrate(min=50%, start=100%, max=120%) per preset
- **Double buffering** — pinned buffers (`GC.AllocateArray<byte>(pinned:true)`), zero-copy к WebRTC
- **High-precision capture loop** — Thread + Stopwatch + SpinWait (~1ms)
- **RTT overlay** — ping/pong через dc-control каждые 3s, EMA smoothing, UI overlay
- **Viewer rendering** — `WriteableBitmap` + unsafe `Buffer.MemoryCopy` + dirty rects

### Features

- **File transfer** — `file_meta` → `file_chunk` (64 KB default) → `file_end`, SHA-256 verification, GZip compression, auto-save `%UserProfile%\Downloads\ZConectReceived`, progress UI, GZip bomb cap 1 MB per decompressed chunk
- **Clipboard sync** — двусторонний, текст до 256 KB UTF-8, защита от эха
- **Display switching on-the-fly** — смена `DisplayId`/`DisplayMode`/`QualityPreset` без переподключения через dc-control request
- **Fast reconnect** — renegotiation offer/answer без полного teardown
- **UAC policy toggle** — через service pipe, `PromptOnSecureDesktop` в HKLM (1 ↔ 0)
- **Auto-ICE** — последовательные попытки host→srflx→relay с фильтрацией кандидатов

### Security hardening (2026-04-18 audit round 1+2 + external audit)

**16 commits за день**, 420/420 unit + LiveHost 9/9 + новые 7 PipeRaceTests. Все fixes под тестовой защитой. Полный список audit findings и fixes в `docs/SECURITY_AUDIT_2026-04-10.md` addendum:

| ID | Где | Что |
|---|---|---|
| C1 | `InputHelperManager.SendToHelperAsync` | TOCTOU на `_helperPipe`; captured local + explicit ObjectDisposed/IOException catch |
| C2 | `InputHelperManager` | `_processLock` вокруг `_helperProcess` accessors |
| C3 | `PipeServer` push methods | Explicit ObjectDisposed handling (DEBUG vs WARN discipline) |
| C4 | `InputHelper.Program` | `JsonDocument.Parse` native-pool leak → `using var doc` |
| C5 | `InputHelper.Program` | `_currentDesktop` → volatile (race pollLoop vs inject) |
| C6 | `ServicePipeClient.SendAsync` | Captured local + explicit ObjectDisposed catch |
| C7 | `MainViewModel._cachedActiveDesktop` | → volatile |
| C9 | `InputHelperManager.ConnectHelperPipeAsync` | Pipe leak on connect failure → try-finally |
| — | `SettingsService.Save` | Atomic write: tempfile → `File.Move(overwrite)` |
| — | `DpapiHelper.Decrypt` | LocalMachine fallback + self-migration to CurrentUser |
| — | `MainViewModel.Uac` | Toggle via service pipe; удалён dead `RestoreUacPolicyIfCrashed` + `UacBackupPath` |

Plus quality improvements (batch 1+2):
- Helper log rotation (5 MB × 3 backups)
- `SendInput` failure → WARN log (вместо silent drop)
- `SetThreadDesktop` failure → skip inject (вместо injection в старый desktop context)
- Removed `ServiceDesktopMonitor` (duplicated helper polling)

External audit findings fixed (P0 + Variant A + D + bonus):

| ID | Severity | Fix |
|---|---|---|
| F-02 partial | HIGH | Hardcoded `TurnPassword = "123456"` → `string.Empty`; regression fix для empty case в `BuildConfiguration` (skip TURN если креды пустые) |
| F-03 | MEDIUM | `DeviceSecret` теперь DPAPI-encrypted |
| F-07 | MEDIUM | Removed dead `GetFtRootJail`/`IsPathAllowedForFt`; explicit design "viewer has full user profile access" documented |
| F-08 | MEDIUM | `_pendingDeleteRequestIds` `HashSet` → `ConcurrentDictionary` |
| F-09 | LOW | Binary parser validates `kind == File` + marker `0x01` |
| F-10 | LOW | `SessionMonitor` сверяет `Process.MainModule.FileName` vs expected (anti-spoof) |
| F-11 | HIGH | `DisableSecureDesktopForRemote` snapshot реального HKLM value перед change (anti-permanent-degrade) |
| F-12 | MEDIUM | `InputHelperManager` JsonDocument leak (мirror C4 но в service-side) |
| F-13 | MEDIUM | DXGI multi-output: copy FrontBuf → BackBuf перед `CompositeFramesTo` (fix stale regions на не-обновившихся мониторах) |
| F-14 | LOW | `SwitchCaptureThreadToActiveDesktop` закрывает previous handle через `_lastCaptureDesktopHandle` |
| BONUS | MEDIUM | DXGI cascading failure fix (`GC.Collect + WaitForPendingFinalizers` перед reinit) |

### Admin panel + server hardening (2026-04-21/22)

**Admin panel** (`server/internal/admin/`) — 10 tabs, session auth (bcrypt cost 12)
+ CSRF + IP whitelist + NDJSON audit log (10 MB rotate × 5 keep). Endpoints
[`/admin/api/*`](API.md#admin-panel-endpoints):
- Обзор / Server Health / Телеметрия / Версия клиента
- **WebRTC сессии** — live list + force Kill + новые колонки (TotalFailedAttempts,
  Tier, Lock countdown)
- **Подозрительные IP** (NEW) — per-IP enumeration counter (5-min sliding window)
  через `internal/bruteforce/ip_tracker.go`. Observability only, автобан НЕ делается
  (user решил manual only — NAT users не страдают)
- **IP блокировки** — manual ban list. Infrastructure для TTL exists (ExpiresAt
  + AutoBanIP helper в BanEntry), но UI только permanent
- Аудит / Логи / Admin сессии / Пароль (password rotation без SSH)
- Maintenance mode toggle → `/api/v1/session/create` возвращает 503
- DB backup download (stream `telemetry.db`)

**Session brute-force protection** (per-session, not per-IP):
- **Progressive lockout**: 15с → 2мин → 15мин → 1ч → StateBlocked permanent
- `TotalFailedAttempts` cumulative counter, `LockoutTier` (0-5), StateBlocked state
- Host разблокирует через `POST /session/refresh?regenerate_pass=true`
- `Retry-After` header + `retry_after_sec` body на 429 → клиент показывает countdown
- 401/423/429/403 responses → client различает и показывает user-facing dialog
  (см. [API.md §5](API.md#5-client-error-handling))

**Auto-update notification** (`032c36a`):
- Server endpoint `/api/v1/client/version` + `cmd/admin-hashpass` CLI
- Client: UpdateChecker service (5с boot + 6ч interval) + pulsing badge в header
- Click badge → open DownloadUrl в default браузере (no auto-install, explicit user decision)

**Client-side UI**:
- Ru/En localization — ~220 resx keys, installer picker + `initial-language.txt`
  seed файл для first-launch default (см. [LOCALIZATION.md](LOCALIZATION.md))
- Presence dots (🟢/🔴/⚪) в Address Book + Recent tab (через `PresenceService`
  polling `/api/v1/presence?logins=...` каждые 30с)
- Quality overlay (Ctrl+I) в Remote Desktop window — live FPS/RTT/bitrate pill
- First-run onboarding 3-step wizard
- Recent row (WrapPanel, gradient avatars по hash имени)

### Production hardening + UX polish (2026-04-25 .. 2026-04-28)

**~35 commits** в worktree `upbeat-almeida` за длинную сессию. Полный лог —
`memory/project_session_2026-04-27.md`.

**HTTPS deploy fallout + bug hunt (2026-04-25)** — `8f15c9d..866ecf3`:
- HTTPS via Caddy + Let's Encrypt на `connect.zconn.ru`, deploy script `-Domain` flag
- Public landing page (`/`) с live uptime + status badges (polls `/api/v1/status`)
- Real client IP fix consolidated в `server/internal/realip` package (10 unit tests)
- mrwebrtc.Dispose() hang mitigation — fire hide event перед dispose + 3 сек timeout
- Maintenance gate расширен на `/join`, `/refresh` (раньше только `/create`)
- UpdateChecker URL scheme whitelist (RCE protection через ShellExecute)
- Atomic ServiceConfig.Save (защита от corruption при power loss)
- Admin IP whitelist CIDR support, HttpClient timeout 30s
- Contact.PresenceBrush `[JsonIgnore]` (fixed JSON stack overflow blocker)
- Legacy URL auto-migration on Load (`http://31.59.45.40` → `https://connect.zconn.ru`)

**Mega-session 2026-04-27** — `457ef8d..ba33806`:

*30-sec WS disconnect debug saga (7 iterations)*:
1. ~~UnattendedAuthCoord теряет subscription~~ — real fix для probe issue, но не drops
2. ~~Server keepalive SetPongHandler~~ — improvement, но drops продолжались
3. ~~Caddy WS timeouts~~ — необходимо но недостаточно
4. ~~pingInterval 30s→15s NAT~~ — улучшение, но не root cause
5. Diagnostic logging добавлено
6. ReadTimeoutSeconds 30→300 — переместило проблему на 5 минут
7. **REAL FIX** (`9262ac0`): two-phase WS receive timeout. Control frames (Ping/Pong)
   обрабатываются ClientWebSocket internally — `CancelAfter` wrapping `ReceiveAsync`
   убивал idle waits. Теперь: первый fragment ждёт unlimited, последующие fragments
   armed на 30s anti-slowloris.

*Bug fixes mega-batch (`457ef8d..1a8d229`)*:
- TURN status: green только после manual refresh → fast_connect path делает
  `RefreshSession` для получения `turn_servers` (`ec957ae`)
- Unattended state lost после crash/restart → setter persists immediately (`5ac4e9e`)
- Onboarding repeat after exit → standalone svc.Save() bypassed Vm._settings (`0623751`)
- Brush cross-thread crash → static frozen brush cache + defensive Freeze (`8547cbd`)
- Confusing `AllowUnattended` checkbox убран — single source of truth = password set (`f41de51`)
- Settings Application tab redesign — Session/Updates/Security группы (`b894d1a`)
- ZConect → ZConnect rename в UI (variant A: scope = visible UI only) (`1a8d229`)
- Version bump 1.0.0 → 1.0.1 (`5ac4e9e`)

*Admin panel: categorized logs viewer* (`79d9a7e`):
- 6 chips: Sessions / WebSocket / Auth / Security / Telemetry / Issues
- Search box + auto-refresh + structured table с color-coded levels/modules
- `apiLogs` extended: category filter + search + structured items + counts

*Android viewer feature parity* (`842b4f1..86b190c`):
- Rotating TURN credentials apply on reconnect
- Shared OkHttpClient (3 variants — api/telemetry/websocket с shared connection pool)
- HTTPS default `https://connect.zconn.ru`, lifecycle-aware Flow collection
- Touch gestures fix: jitter 12→30px, double-click → 2× sendTap, hold-drag mode,
  2-finger pan when zoomed
- GestureHelpDialog `fillMaxHeight(0.90f)` (был cropped в landscape)
- L10n RU/EN — 110+ keys, `values/strings.xml` + `values-ru/strings.xml`,
  4 главных screen'а (Connect, AddressBook, Remote, ContactForm)
- **UnattendedAuthCoordinator port** (Variant A) — PBKDF2 100k + HMAC-SHA256
  password proof. `data/UnattendedAuthCoordinator.kt` ~270 строк, mirrors WPF logic
- `SCREEN_ORIENTATION_USER` (был `SENSOR_LANDSCAPE` — force landscape убран)
- Toolbar 2-row layout с `horizontalScroll` (fix portrait overflow с 13 IconButtons)

*Variant 1: 3 Android security audit fixes* (`ba33806`):
- `SecurePrefs.kt` (NEW) — EncryptedSharedPreferences (AES-256-GCM, master key
  в Android Keystore) + `InMemorySharedPreferences` fallback + one-shot migration
  (login/passCode из AppSettings → encrypted store)
- Per-contact saved password — `getContactPassword/setContactPassword/removeContactPassword`
  helpers, `Remember password` checkbox в `UnattendedPasswordDialog`, silent
  reconnect через `savedPassword` parameter в coordinator
- Lockout countdown timer — `LaunchedEffect` tick'ает каждую секунду, dynamic
  display 0:00 instead of static lockout duration

### Quality-of-life fixes from user testing (2026-04-18 afternoon)

- **InputInjectionAgent WER crash dumps** — `OperationCanceledException` из `GetConsumingEnumerable` бросалась на `foreach` MoveNext (ДО входа в body), `catch` внутри body не ловил → unhandled → WER dump каждый service stop (5 dumps накоплено). Wrapped в outer try/catch. (`2798573`)
- **FT delete большой папки hang UI** — `Directory.Delete(recursive:true)` sync на UI thread → 15GB dump. Переделано в `DeleteLocalItemsAsync` с `Task.Run`. (`03b7dc9`)
- **FT sort не работал в правой панели remote mode** — setter re-sort'ит RightItems in-place вместо `RefreshRight()` (который только для local). (`03b7dc9`)
- **ICE label вводил в заблуждение** — всегда показывал "LAN (прямое)" для host type. Now классифицирует по IP scope: LAN IPv4 / ULA IPv6 (VPN) / Teredo / link-local / loopback / public. (`2f952d6`)
- **Installer clean uninstall** — taskkill service + `UninstallDelete filesandordirs {app}` sweep (`1d01d49`)
- **Testability refactor** — `ServicePipeClient` получил configurable pipe name для PipeRaceTests

### Installer

- **InnoSetup 6.6.0** (dark polar theme) — `installer/ZConect.iss`, build скрипт `installer/build.ps1`
- Three exe published: ZConnect.exe, ZConectService.exe, ZConectInputHelper.exe
- Service install + start + firewall rule в `[Run]`
- Clean uninstall — taskkill всех 3 exe + `UninstallDelete filesandordirs {app}` финальный sweep (fix 2026-04-18)
- Pairing data save/wipe prompt на uninstall

### Tests

**Go server-side** — **76 tests** (`server/internal/...`):
- `session` (19) — progressive lockout tiers, TotalFailedAttempts cumulative,
  StateBlocked exhausted tiers, refresh unblocks, concurrent race safety
- `admin` handler (26) — login rate limit, CSRF, password change, WebRTC kill,
  audit, client version, maintenance, IP ban add/list/remove, 9/10 admin tabs covered
- `admin` ban (10) — TTL expire (lazy cleanup), AutoBanIP extend, sanitizeReason
- `bruteforce` (10) — IPTracker windows, min filter, sort, concurrent, memory cap
- `ratelimit` (11) — per-IP, windows, X-Forwarded-For trust rules, middleware 429

**Client-side C#** — 440+ unit tests + live integration:
- 440+ unit (non-Live): SafePath, IceUpgrade, WsReconnection, CursorShape,
  AddressBook, FileTransfer (gzip bomb, folder download, queue, paths), PipeRace,
  ServiceLifecycle, BootIntegration, NetworkResilience, LogAudit, UacDesktop
- **LiveServerTests (43 passed, 1 skipped)** — configurable через env vars
  (`ZCONECT_TEST_SERVER`/`_WS`). Default = prod IP, но CI script
  [`tools/run_integration_tests.ps1`](../tools/run_integration_tests.ps1) поднимает
  **local Go server** (builds + start + healthz + dotnet test + cleanup, ~25 сек).
  Покрытие: API CRUD, WS auth/relay, peer limit, body limit, brute-force tiers,
  Retry-After header, JoinSessionStatus mapping, session blocked/locked flows
- 1 LiveHost Fact (9 scenarios, 54s): Video, FT large file, path traversal, RTT,
  GZip compression, quality switch, Winlogon CAD recovery — требует real
  host client в worktree

## 🟡 В процессе / приоритетный backlog

- **SIPSorcery migration** — уход от `Microsoft.MixedReality.WebRTC 2.0.2` (архивирован Microsoft). Критичный технический долг: текущий `mrwebrtc.dll` 2.0.2 из 2020, без security fix'ов за 5 лет.
- ~~**HTTPS/TLS на signaling**~~ → DONE 2026-04-24 (Caddy + Let's Encrypt на `connect.zconn.ru`).
- ~~**TURN password rotation**~~ → DONE 2026-04-24 (HMAC short-term creds RFC 7635, см. `TURN_ROTATION.md`).
- **Code signing installer** — без подписи Windows SmartScreen блокирует скачивание для новых пользователей.
- ~~**Auto-update mechanism**~~ — DONE: notification-only (`032c36a`). Client badge + click → browser DownloadUrl. Silent install — explicit user decision.
- **L10n cleanup round 3** — ~50 оставшихся hardcoded Russian strings в
  FileTransferWindowViewModel / Uac / Video / transfer status / relative time.
  См. [`project_localization.md`](../../../../Users/zahar/.claude/projects/C--Soft-pub-ZConect/memory/project_localization.md) deferred list.
- **Integration tests expansion** — 10 tier'ов в [`project_integration_test_roadmap.md`](../../../../Users/zahar/.claude/projects/C--Soft-pub-ZConect/memory/project_integration_test_roadmap.md)
  (admin↔session cross-cutting, WS edge cases, FT через real DC, security, perf).
- **Android viewer v2 UX** — DONE 2026-04-22 (commits `636f5c6..e510bdd`):
  Recent row, AddressBook search/sort/avatars/presence dots, detailed error handling
  с Retry-After countdown, save-to-contacts из Recent, Back button → disconnect.
- **Android viewer feature parity** — DONE 2026-04-27 (commits `842b4f1..ba33806`):
  Rotating TURN apply on reconnect, HTTPS default + shared OkHttpClient, touch
  gestures fix (double-click + hold-drag + zoom pan), L10n RU/EN (110+ keys),
  UnattendedAuthCoordinator port (PBKDF2 100k + HMAC-SHA256), respect device
  rotation, 2-row toolbar, EncryptedSharedPreferences + per-contact saved password
  + lockout countdown. См. `memory/project_android_viewer.md` (полный rewrite
  2026-04-27 с 26 идеями для дальнейших улучшений).
- **Android viewer v3 backlog** — clipboard sync, file transfer, multi-session tabs,
  dark theme toggle, certificate pinning, code signing APK. Список в
  `project_android_viewer.md` (HIGH/MED/LOW priority).
- **macOS / Linux viewer** — Android MVP уже есть (worktree upbeat-almeida, commit b590f94). Для mass adoption нужен хотя бы macOS.

## 🟢 Известные ограничения (by design / kernel-level)

- **Win+L lock screen clicks не работают** — Windows kernel filter'ит synthetic input на secure desktop независимо от integrity level процесса. Требует WHQL-signed kernel driver (как у TeamViewer/AnyDesk).
- **Win+L cursor reset на arrow** — host не может детектить cursor handle на secure desktop через `GetCursorInfo` под user/helper token.
- **DXGI cascading failure** — иногда после первого `winlogon_dxgi_failed_gdi` DXGI на Default тоже ломается с E_ACCESSDENIED. Simple `GC.Collect` fix в TODO, deferred.
- **HW encoding (H.264/HEVC)** — NVENC/QSV/AMF deferred, не работает на VM где разрабатывается.
- **mrwebrtc 192kHz audio sample rate** — known issue с native binary, deferred.
- **Single signaling server** — SPOF для production. Требует DNS round-robin или load balancer.
- **IPv4 only** — явного блокирования IPv6 нет, но не тестировано.

## Документация (состояние)

| Документ | Статус |
|---|---|
| `ARCHITECTURE.md` | ✅ Обновлён 2026-04-18 (three-process, verified against code) |
| `IMPLEMENTATION_STATUS.md` | ✅ Обновлён 2026-04-28 (этот файл) |
| `ROADMAP.md` | ✅ Обновлён 2026-04-28 (Iteration 12 добавлена) |
| `TURN_ROTATION.md` | ✅ Создан 2026-04-24 (HMAC short-term creds) |
| `LOCALIZATION.md` | 🟡 Добавить Android L10n Phase 1 (strings.xml ru+en) |
| `SECURITY_AUDIT_2026-04-24.md` | ✅ Создан, fix-ы applied |
| `SERVICE.md` | 🟡 Обновить (InputHelperManager, убрать ServiceDesktopMonitor) |
| `API.md` | 🟡 Обновить pipe message types + categorized logs endpoint |
| `PIPE_PROTOCOL.md` | 🆕 Создать (каноническая спецификация) |
| `LOGGING.md` | 🟡 Добавить log rotation в helper + categorized logs (admin) |
| `CLIENT_SETTINGS.md` | 🟡 Минор (DPAPI migration + UiLanguage) |
| `RELEASE_V1.md` | 🔄 Переработать в `BETA_CHECKLIST.md` |
| `DEPLOY_GUIDE.md` | ✅ Обновлён 2026-04-24 (HTTPS Caddy, -Domain flag) |
| `DEPLOY_UBUNTU24.md`, `DEPLOY_FROM_WINDOWS.md` | ⚪ Проверить актуальность |
| `GITHUB_PUBLISH.md` | ⚪ Проверить актуальность |
| `TODO_SERVICE_V2.md` | ❌ Устарел — удалить |
