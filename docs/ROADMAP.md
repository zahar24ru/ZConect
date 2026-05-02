# Roadmap

Последнее обновление: 2026-04-28. Текущая версия: **1.0.1** (release `5ac4e9e`).

## ✅ Iteration 12 — Disconnect debug + Android feature parity + UX polish (DONE 2026-04-27..28)

**~35 commits** в worktree `upbeat-almeida` за длинную сессию. Полный лог
коммитов и iteration deep-dive: `memory/project_session_2026-04-27.md`.
Release tagged: **1.0.1**.

### 30-sec WebSocket disconnect debug saga (7 iterations) — `9262ac0` real fix

Hosts падали через 30 секунд idle. 7 гипотез по очереди:
1. UnattendedAuthCoord теряет subscription на reconnect (`f7404bc`) — real fix для probe issue, но не drops
2. Server keepalive SetPongHandler (`f3f66bf`) — improvement, drops продолжались
3. Caddy WS proxy timeouts (`e18c6f0`) — необходимо но недостаточно
4. pingInterval 30s→15s NAT traversal (`59de7cb`) — улучшение, но не root cause
5. Diagnostic logging (`69d1647`)
6. ReadTimeoutSeconds 30→300 (`228c0c9`) — переместило проблему на 5 минут
7. **REAL FIX** (`9262ac0`): two-phase WebSocket receive timeout. Control frames
   (Ping/Pong) обрабатываются `ClientWebSocket` internally — `CancelAfter` wrapping
   `ReceiveAsync` убивал idle waits. Теперь: первый fragment ждёт unlimited,
   последующие fragments armed на 30s anti-slowloris.

### Bug fixes mega-batch (`457ef8d..1a8d229`, `8547cbd..f41de51`)

- [x] TURN status: green только после manual refresh → fast_connect path делает `RefreshSession` для получения `turn_servers` (`ec957ae`)
- [x] Unattended state lost после crash/restart → setter persists immediately (`5ac4e9e`)
- [x] Onboarding repeat after exit → standalone svc.Save() bypassed Vm._settings (`0623751`)
- [x] Brush cross-thread crash → static frozen brush cache + defensive Freeze (`8547cbd`)
- [x] Confusing `AllowUnattended` checkbox убран — single source of truth = password set (`f41de51`)
- [x] Settings Application tab redesign — Session/Updates/Security группы (`b894d1a`)
- [x] ZConect → ZConnect rename в UI (variant A: scope = visible UI only) (`1a8d229`)
- [x] Version bump 1.0.0 → 1.0.1 (`5ac4e9e`)

### Admin panel: categorized logs viewer (`79d9a7e`)

- [x] 6 chips для quick filter: Sessions / WebSocket / Auth / Security / Telemetry / Issues
- [x] Search box + auto-refresh + structured table с color-coded levels/modules
- [x] `/admin/api/logs` extended: category filter + search + structured items + counts
- [x] L10n keys для всей UI (RU/EN consistent)

### Android viewer feature parity (`842b4f1..86b190c`)

- [x] **Rotating TURN apply on reconnect** — `842b4f1`. Раньше Android получал new creds но не применял на existing PeerConnection
- [x] **Shared OkHttpClient** — `dc035f6`. Singleton pool с 3 variants (api/telemetry/websocket), все share connection pool. + lifecycle-aware Flow collection
- [x] **HTTPS default** `https://connect.zconn.ru` (был hardcoded http IP) — `dc035f6`
- [x] **Touch gestures fix** — `990fed8`. Jitter threshold 12→30px, double-click → 2× sendTap, hold-drag mode при долгом нажатии, 2-finger pan when zoomScale > 1
- [x] **GestureHelpDialog landscape fix** — `3bb660b`. `fillMaxHeight(0.90f)` (был cropped)
- [x] **L10n RU/EN полный** — `ad89c07`. 110+ keys в `values/strings.xml` + `values-ru/strings.xml`. ConnectScreen, AddressBookScreen, RemoteScreen, ContactForm
- [x] **UnattendedAuthCoordinator port (Variant A)** — `ca3dfc4`. PBKDF2 100k + HMAC-SHA256 password proof. ~270 строк mirroring WPF logic. Bridge с Compose через `CompletableDeferred<PromptResult>`
- [x] **Respect device rotation** — `2c5eff4`. `SCREEN_ORIENTATION_USER` (был `SENSOR_LANDSCAPE` — force landscape убран по user request)
- [x] **2-row toolbar layout** — `86b190c`. `horizontalScroll` modifier, fix portrait overflow с 13 IconButtons

### Variant 1: 3 Android security audit fixes (`ba33806`)

- [x] **EncryptedSharedPreferences** — `data/SecurePrefs.kt` (NEW). AES-256-GCM, master key в Android Keystore, `InMemorySharedPreferences` fallback для legacy devices, one-shot migration (login/passCode из old AppSettings → encrypted store)
- [x] **Per-contact saved password** — `getContactPassword/setContactPassword/removeContactPassword` helpers, "Remember password" checkbox в `UnattendedPasswordDialog`, silent reconnect через `savedPassword` parameter в `UnattendedAuthCoordinator.run()`
- [x] **Lockout countdown timer** — `LaunchedEffect` tick'ает каждую секунду, dynamic display (0:00 при истечении) instead of static `lockoutRemainingSec`

### Toolchain upgrade

External edit (linter/IDE) подвинул AGP 8.13.2 → **9.1.1**, Kotlin 2.1.0 →
**2.2.10** в `gradle/libs.versions.toml`. Не блокировало сборку.

### Pending verification

- [ ] User test of Variant 1: EncryptedSharedPreferences migration на existing install + per-contact saved password silent reconnect + lockout countdown ticking
- [ ] HTTPS connect.zconn.ru install/uninstall full cycle (legacy URL migration, wipe verification)

## ✅ Iteration 11 — Production hardening (DONE 2026-04-24..25)

After HTTPS deploy + bug hunt — 11 коммитов выпустили production-ready stack:
`8f15c9d..866ecf3`. Серверная часть deployed на prod (verified: real IP в логах
вместо Caddy docker IP), installer пересобран `ZConect-Setup-1.0.0.exe`.

- [x] HTTPS via Caddy + Let's Encrypt автоматически (`8f15c9d..a419631`)
- [x] Public landing page on `/` с live uptime + status badges (`451e3d3`)
- [x] Rotating TURN credentials RFC 7635 (`f54900e`) — per-client HMAC creds, default TTL 30 мин
- [x] Real client IP via Caddy XFF + CIDR consolidated в `server/internal/realip` (`2812da3`)
- [x] mrwebrtc.Dispose() hang mitigation — 3 сек timeout, fire hide event раньше (`4096e3f`)
- [x] Maintenance mode gate расширен на /join + /refresh (`18311a3`)
- [x] UpdateChecker URL scheme whitelist (RCE protection через ShellExecute) (`18311a3`)
- [x] Atomic ServiceConfig.Save (защита от corruption при power loss) (`18311a3`)
- [x] Android DEFAULT_TURN_PASS="123456" purged (`18311a3`)
- [x] Admin IP whitelist CIDR support (`18311a3`)
- [x] HttpClient timeout 30s + PresenceService chunking >100 (`18311a3`)
- [x] Contact.PresenceBrush [JsonIgnore] (fixed JSON stack overflow blocker) (`18311a3`)
- [x] Legacy URL auto-migration on Load (`866ecf3`)

### Pending verification
- [ ] User install test: upgrade поверх existing install (migration срабатывает) → uninstall с wipe → fresh install
- [ ] TURN Status UI redesign (Variant C minimum) — В РАБОТЕ

## ✅ Iteration 1 — Session & Signaling (v1.0.0)

- [x] Go signaling service (create/join/close/refresh/healthz)
- [x] In-memory session storage (TTL, attempts, lock)
- [x] Session codes 8+8 digits
- [x] Structured JSON logging
- [x] Session ownership auth (owner_secret)
- [x] Device secret for machine binding
- [x] Signaling message validation (block reserved types)
- [x] Rate limiting with trusted proxies

## ✅ Iteration 2 — WebRTC + Video (v1.0.0)

- [x] Peer connection via Microsoft.MixedReality.WebRTC
- [x] ICE/STUN/TURN (coturn)
- [x] DXGI screen capture (single + multi-monitor + GDI fallback)
- [x] VP8 video codec
- [x] Quality profiles: Extra Low / Low / Medium / High / Auto
- [x] Auto-quality adaptation
- [x] Dirty rects + double-buffered pinned arrays

## ✅ Iteration 3 — Remote Control & UX (v1.0.0)

- [x] Mouse/keyboard injection via WinAPI
- [x] Cursor shape sync (remote rendering, not compositing)
- [x] Display selection (DISPLAY1..N, All)
- [x] ICE status with route type and IP
- [x] P2P-aware reconnect UX

## ✅ Iteration 4 — Clipboard + File Transfer (v1.0.0)

- [x] Bidirectional clipboard sync (text, 256 KB)
- [x] Two-panel file manager with remote ops
- [x] Upload/download files and folders
- [x] Pause/Resume/Cancel + SHA-256 + GZip (bomb cap 1 MB per chunk)
- [x] File conflict dialog, drag & drop, inline rename
- [x] View modes: Details / Small / Large icons

## ✅ Iteration 5 — Stabilization & Security (v1.0.0)

- [x] Structured logging with rotation (5 MB, 3 backups)
- [x] DPAPI encryption for local secrets
- [x] Path traversal protection + security audit (30+ findings fixed)
- [x] Docker Compose deploy from Windows

## ✅ Iteration 6 — Service & Portable Mode (v1.0.0)

- [x] Windows Service (SYSTEM token)
- [x] Named Pipe IPC (Service ↔ GUI)
- [x] Auto-install service on first launch + auto-restart
- [x] Unattended sessions with machine_id + device_secret
- [x] Address book with contacts

## ✅ Iteration 7 — Security Audit + Telemetry (v1.0.0)

- [x] NET-05..N7-09: viewer gate, WS token in auth, body limit, DoS guards, log rotation, secrets masking
- [x] Telemetry dashboard (SQLite, heartbeat, GeoIP, web UI)
- [x] Dockerfile Go 1.25 + Alpine 3.21 (CVE fixes)

## ✅ Iteration 8 — Three-process architecture (2026-04-18)

Фундаментальный refactor для работы UAC/Winlogon input:

- [x] Stage 1: UI под user token вместо winlogon token (устранило DPAPI CurrentUser + %APPDATA% проблемы)
- [x] Stage 2: input injection делегирован в service pipe (UI больше не вызывает SendInput сам в обычном режиме)
- [x] Stage 3: ZConectInputHelper.exe — отдельный процесс под winlogon token в user session; все input + desktop polling там
- [x] UAC clicks работают — helper's System integrity обходит UIPI barrier
- [x] Win11 Ctrl+Alt+Del работает через `SendSAS(asUser=false)` из session 0 InputInjectionAgent (без kernel driver, без LockWorkStation fallback)
- [x] UAC policy toggle через service pipe (HKLM write требует SYSTEM)
- [x] `PER_MONITOR_AWARE_V2` DPI awareness в helper (правильные координаты на scaled дисплеях)

## ✅ Iteration 9 — Deep audit round 1+2 (2026-04-18)

11 CRITICAL/HIGH fix'ов под тестовой защитой (420/420 unit + LiveHost 9/9):

- [x] C1, C6: pipe SendAsync TOCTOU на обеих сторонах (service + UI)
- [x] C2: `_helperProcess` lock в InputHelperManager
- [x] C3: PipeServer push methods — explicit ObjectDisposed handling
- [x] C4: JsonDocument native-pool leak в helper
- [x] C5, C7: volatile для `_currentDesktop` + `_cachedActiveDesktop`
- [x] C9: pipe leak on ConnectHelperPipeAsync failure
- [x] Settings atomic write (tempfile + rename)
- [x] DPAPI LocalMachine fallback + self-migration
- [x] UAC dead code removed (direct HKLM write unreachable под user token после Stage 1)
- [x] 7 new `PipeRaceTests` — concurrent sends, burst-during-disconnect, reconnect scenarios, protocol roundtrips
- [x] Helper log rotation (5 MB × 3 backups)
- [x] `SendInput` failure → WARN log (fix: silent drop)
- [x] `SetThreadDesktop` failure → skip injection (fix: wrong desktop injection)
- [x] Removed `ServiceDesktopMonitor` (duplicate of InputHelper polling)
- [x] Installer clean uninstall (taskkill + `UninstallDelete filesandordirs {app}`)

## 🔴 Iteration 10 — Beta readiness (current)

Блокеры для public beta — должно быть все сделано перед релизом:

### Критично
- [x] ~~**HTTPS/TLS на signaling**~~ → DONE 2026-04-24 (commits `8f15c9d..a419631`): Caddy reverse proxy + Let's Encrypt auto-provisioning, HTTPS-aware deploy scripts + client (WPF auto-detects scheme, Android `AppSettings.httpBaseUrl/wsBaseUrl` auto-switches ws/wss), full docs (HTTP-vs-HTTPS decision guide в root README/deploy/README/DEPLOY_GUIDE).
- [x] ~~**TURN password rotation**~~ → DONE 2026-04-24: HMAC-based short-term credentials (RFC 7635 / coturn `--use-auth-secret`). Per-client unique creds с expiration 30 мин. Signaling возвращает `turn_servers` в session create/join/refresh responses. Secret auto-generated deploy script'ом (32 bytes hex). Клиенты (WPF + Android) парсят и используют dynamic creds вместо static. 11 unit tests. Подробности: [`TURN_ROTATION.md`](./TURN_ROTATION.md). **Post-deploy fix** (`4fd3216`): AutoRefresh path игнорировал turn_servers из refresh response.
- [x] ~~**Real client IP через reverse proxy**~~ → DONE 2026-04-24 (`2812da3`): все 4 handler'а (api/admin/telemetry/ratelimit) возвращали Caddy docker IP (172.18.0.4) вместо реального IP. Причина — `TRUSTED_PROXIES` CIDR (`172.16.0.0/12`) не парсился (exact-match map), плюс api/handler вообще не смотрел XFF. Consolidated в новый `server/internal/realip` package с full CIDR + X-Real-IP + X-Forwarded-For поддержкой. 10 unit tests.
- [x] ~~**mrwebrtc.Dispose() hang mitigation**~~ → DONE 2026-04-24 (`4096e3f`): известный deadlock на disconnect стопил cleanup → orphaned Remote Desktop window. Fix: fire RemoteScreenWindowShouldHide ДО dispose + 3 сек timeout на Dispose с abandon. Остальная managed cleanup выполняется гарантированно.
- [ ] **Code signing** (опционально для OSS) — SignPath.io Free для open-source, или Azure Trusted Signing (~$10/мес), или без подписи (SmartScreen требует клика "Выполнить в любом случае" один раз)
- [ ] **SIPSorcery migration** — `Microsoft.MixedReality.WebRTC` archived Microsoft'ом, `mrwebrtc.dll 2.0.2` из 2020 без security fix'ов 5 лет. **High security risk**
- [x] ~~**Auto-update mechanism**~~ → DONE 2026-04-21 (notification-only, `032c36a`). Silent install отложен как explicit user decision — сейчас badge + клик открывает DownloadUrl в браузере.
- [ ] **Crash reporting** — Sentry или собственный endpoint. Без этого edge-case баги не видны

### Важно
- [ ] **Privacy Policy + Terms of Service** — обязательно для коммерциализации
- [ ] **Rate limiting на signaling endpoints** — усиление против DoS (сейчас default 200/min, нужно расширить на per-endpoint)
- [ ] **Docs pass** — doc cleanup (этот pass 2026-04-18)
- [ ] **Многомониторный выбор в UI** — проверить что работает на 3+ мониторах

## ✅ Iteration 10.5 — ICE hardening (DONE 2026-04-19)

После тестирования на реальной топологии (VPN + Teredo) выявлены ICE improvements. Подробности в `project_ice_p0_hardening.md` memory.

### P0 (done commits 6c29948 + b8f5025)
- [x] **ICE `Disconnected` state handling** — `IceStateDisconnected`/`Reconnected` events, UI banner «Соединение нестабильно — восстанавливаем...»
- [x] **ICE Restart на Failed** — Viewer сразу `StartIceRestartAsCallerAsync` fire-and-forget, Host 8s grace period перед teardown с `_iceFailedGraceToken`
- [x] **Multi-TURN failover** — `TurnServers[]` в `TransportSettings` + `ClientSettings`, backward compat к single `TurnUrl`
- [x] **Network change auto-detect** — было реализовано в `IceUpgradeOptimizer` (подписан на `NetworkAddressChanged`/`NetworkAvailabilityChanged`)
- [x] **Force relay / Disable TURN toggles** в UI — Settings → «Соединение», mutually exclusive
- [x] **Restart throttling** — max 1 restart/10с + in-flight flag (защита от flood на flaky сетях)

### P1 (done commit b8f5025)
- [x] **Connection quality overlay** — Ctrl+Shift+I toggle, RTT/Mbps/drop%/route через `GetSimpleStatsAsync` → `VideoReceiverStats`
- [x] **Real selected ICE pair** — `IceRouteDisplayLabel` резолвит relay IP → TURN host, structured log `ice_selected_pair` на promotion
- [x] ~~**TURN credentials rotation**~~ → DONE 2026-04-24 (RFC 7635 / ephemeral creds, см. выше)

### P2 (nice to have)
- [ ] Prefer IPv6 option
- [ ] Candidate filtering (phantom interfaces block)
- [ ] ICE gathering timeout cap

### P3 (edge cases)
- [ ] User-visible Teredo/VPN detection warning
- [ ] Detailed candidate viewer в debug-log

## ✅ Iteration 10.6 — FT fixes + bug sweep (DONE 2026-04-19)

- [x] **DXGI cascading failure** — было сделано в commit be1ed17 (`GC.Collect + WaitForPendingFinalizers + Thread.Sleep(300)` в `ReinitOnNewDesktop`)
- [x] **mrwebrtc 192kHz audio preflight** — `AudioPreflight.Check()` через WASAPI `IMMDeviceEnumerator` + `IAudioClient.GetMixFormat` на все active endpoints, hard-exit с friendly name через `IPropertyStore` (commits 3af60b2 → 8b01452)
- [x] **FT #2 — progress 100% до ack** — `IsAwaitingAck` в `TransferQueueProgress`, StatusText «Ожидание подтверждения получателем...»
- [x] **FT #3 — conflict dialog** — перенесён на viewer side через round-trip DC messages (`FileConflictQuery`/`Response`), routing по role: receiver-is-viewer→local, sender-is-viewer→remote (commits 9ce493b → 2d05ebc)
- [x] **FT: UnobservedTaskException fix** — `FireAndForget(Task, context)` helper ловит faults в detached send'ах (commit abead69)
- [x] **Live FT test infrastructure** — 13 сценариев в `LiveFileTransferConflictTests.cs` (upload + download + conflict + stress 20/100 files + unicode + cancel + path traversal + folder structure + conflict timeout); 10/13 passed, 3 `[Fact(Skip=...)]` с FIXME. Env vars: `ZCONECT_HOST_LOGIN`/`ZCONECT_HOST_PASS`/`ZCONECT_HOST_SAVE_DIR` (commits baca2ce, 6be86ce, 59135ff)
- [ ] **#1 Scrim при viewer connect** — нужен repro сценарий (by-design или stuck после dialog?)

## 🟠 FT investigations pending (FIXME в live tests)

3 скрытых узких места найденных live тестами 2026-04-19:

- [ ] **`FT_file_request_missing_path_yields_error`** — host не шлёт `FileError` за 15s на несуществующий файл. По коду ([MainViewModel.FileTransfer.cs:229](client/UiApp/ViewModels/MainViewModel.FileTransfer.cs:229)) должен. Investigate host logs.
- [ ] **`FT_conflict_overwrite_all_no_query_on_subsequent`** — `queryCount != 1`. `_conflictActionForAll` не persist'ится через remote flow, либо double-subscriber в тесте. Investigate.
- [x] **`FT_large_file_60mb`** — **CLOSED 2026-04-19** (commit a3e1d7f). 4.5 MB/s upload + 8.4 MB/s download без VPN. Previous 200 KB/s был VPN+Teredo overhead.
- [x] **`FT_file_request_missing`** — **CLOSED 2026-04-19** (commits a203544 + d89b152). Silent return → explicit FileError + DC channel wait.
- [x] **`FT_conflict_overwrite_all`** — **CLOSED 2026-04-19** (commits 559eac7 + addb957). DC thread deadlock из-за .Wait blocking → Task.Run offload + lock serialization.

## ✅ Iteration 10.8 — Integration test foundation (DONE 2026-04-22)

Commits `ca15750` (session brute-force + 66 Go tests) + `5dadda4` (local server +
C# LiveServerTests pipeline). Foundation для cross-cutting client+server тестов.

- [x] **Session brute-force** — progressive lockout 15с→2m→15m→1h→BLOCKED permanent,
      per-session (не per-IP для NAT-safe), Admin panel shows TotalFailedAttempts +
      LockoutTier + countdown. 19 Go unit tests.
- [x] **Admin handler tests** — 26 integration-style C# tests на login/CSRF/password/
      WebRTC sessions/audit/ban/maintenance/client version.
- [x] **Ratelimit tests** — 11 tests: windows, XFF trust, middleware, concurrent.
- [x] **LiveServerTests env-configurable** — ZCONECT_TEST_SERVER / ZCONECT_TEST_WS
      env vars → можно гонять против local (127.0.0.1:8099) или prod.
- [x] **Pipeline runner** — `tools/run_integration_tests.ps1`: build Go server,
      start на random port, wait healthz, run dotnet test, kill server, cleanup.
      ~25 сек full cycle.
- [ ] **Future tiers** (10 total) — documented в `project_integration_test_roadmap.md`:
      admin↔session cross-cutting, WS edge cases, FT через real DC, security, perf.

Test counts now: **76 Go unit + 43 C# live tests passed** against local server.

## ✅ Iteration 10.9 — Client notification UX для server rejections (DONE 2026-04-22)

Commits `54febfb` (log clarity) + `6d65c31` (client dialogs) + `e38a5a5` (L10n round 3).

Когда server отказывает в create/join — client показывает локализованное user-facing
сообщение (раньше был generic "ошибка подключения").

- [x] **HTTP response contract** — 403 banned, 423 session blocked (permanent),
      429 session locked (temp with Retry-After header + retry_after_sec body).
- [x] **session.Service.LockRemaining(login_code)** — helper для вычисления
      Retry-After seconds для 429 response.
- [x] **SessionApiClient.CreateSessionAsyncDetailed / JoinSessionAsyncDetailed** —
      возвращают typed Status enum (Success/Banned/Maintenance/SessionLocked/
      SessionBlocked/NetworkError/ServerError) + HttpStatus + RetryAfterSec.
- [x] **MainViewModel.Connection.HandleCreate/JoinFailure** — MessageBox для
      critical (ban, blocked, maintenance), status bar для transient (locked, invalid).
- [x] **Log message clarity** — `session_refreshed` (misleading "password refreshed")
      разбит на `session_pass_regenerated` (INFO, actual pass change) vs
      `session_ttl_extended` (DEBUG, keepalive noise).
- [x] **Localization round 3** — viewer approval dialog, ContactForm titles,
      Session countdown, HostStats format, all status messages.

## ✅ Iteration 10.7 — Localization Ru/En (DONE 2026-04-22)

Commits `f4253f3..001de48` (6 commits). Подробности в `docs/LOCALIZATION.md` и
`project_localization.md` memory.

- [x] **i18n framework** — `Strings.resx` (ru-RU primary) + `Strings.en-US.resx` (satellite) + hand-maintained `Strings.Designer.cs`. `<NeutralLanguage>ru-RU</NeutralLanguage>` в csproj.
- [x] **Full XAML extraction** — все 10+ XAML файлов (Main/Settings/RemoteScreen/FileTransfer + 4 Dialogs) используют `{x:Static props:Strings.*}`.
- [x] **ViewModels extraction** — core Status_* messages (MainViewModel + .Connection + .AddressBook) через `Strings.Status_*`.
- [x] **Code-behind extraction** — tray menu, toasts, Help (?), theme tooltips, update-check results, onboarding, password strength, lockout UI, all confirm prompts.
- [x] **Settings toggle** — Настройки → Приложение → Язык с dropdown (Русский/English). Persist в `ClientSettings.UiLanguage`, применяется на restart.
- [x] **Fix**: `UiLanguage` не сохранялся — `Vm.SaveSettings()` перезаписывал stale "" из `_settings`. Fix через `public UiLanguage` property backed by `_settings`.
- [x] **Installer language picker** — `ShowLanguageDialog=yes`, `[Languages]` Russian + English, `[CustomMessages]` для service progress + uninstall prompts.
- [x] **Installer → GUI language seed** — `CurStepChanged(ssPostInstall)` пишет `C:\ProgramData\ZConect\initial-language.txt`. App на первом запуске читает seed и применяет как default (если `UiLanguage` пустой).
- [ ] **Round 3 cleanup** (~50 hardcoded strings в FileTransferWindowViewModel, Uac/Video VMs, WsReconnectionManager, relative time, transfer queue status) — deferred.

**Developer rule** (docs/LOCALIZATION.md): все новые user-visible strings
добавляются в ОБЕ локали одним коммитом. Pre-commit check через grep Russian в
`git diff`.

## 🎨 UI simplification (planned)

- [ ] **Remote desktop button → Task Manager button** — вместо full-screen desktop session дать кнопку «Диспетчер задач». Основной support use case (kill hung process), узкий scope, меньше данных через P2P. Implementation: `Process.Start("taskmgr")` на host'е через InputAgent или DC command.
- [ ] **Убрать Ctrl+Alt+Del кнопку** из UI — уже работает через session 0 (SOLVED 2026-04-17) но не нужна для typical use case.

## 🟡 Iteration 11 — Post-beta / v2.0

- [ ] **macOS viewer** (Electron + wasm webrtc? Или native Swift с WebRTC Swift binding?)
- [ ] **Linux viewer** (GTK + webrtc-gst)
- [x] ~~**Android viewer v2 — UX parity Phase 1+2**~~ DONE 2026-04-22 (commits `636f5c6..e510bdd`):
  Recent row на ConnectScreen, gradient avatars, presence dots (🟢/🔴/⚪) polling
  /api/v1/presence каждые 30с, Address Book search/sort/avatars/relative time,
  detailed error handling (banned/blocked/locked с Retry-After), save-to-contacts
  из Recent, Back button → disconnect (не close app), keyboard hide toggle.
- [x] ~~**Android viewer feature parity**~~ DONE 2026-04-27 (commits `842b4f1..ba33806`):
  Rotating TURN apply on reconnect, HTTPS default + shared OkHttpClient, touch
  gestures fix (double-click + hold-drag + zoom pan), L10n RU/EN (110+ keys),
  UnattendedAuthCoordinator port (PBKDF2 100k + HMAC-SHA256 + lockout), respect
  device rotation, 2-row toolbar layout. Variant 1 security audit:
  EncryptedSharedPreferences + per-contact saved password + lockout countdown timer.
- [ ] **Android viewer v3** — full feature parity: clipboard sync, file transfer,
  multi-session tabs, dark theme toggle, certificate pinning, code signing APK.
  Backlog 26 идей в `memory/project_android_viewer.md` (HIGH/MED/LOW priority).
- [ ] **iOS viewer**
- [ ] **Session recording / audit log** — для compliance
- [ ] **Admin console** — remote device management, fleet view
- [ ] **Chat during session**
- [ ] **Multi-display viewer grid** — несколько remote-screens сразу
- [ ] **Hardware encoding** (NVENC/QSV/AMF для H.264/HEVC)
- [ ] **File transfer resume**
- [ ] **Shared clipboard rich formats** (images, files)
- [ ] **Wake-on-LAN integration**
- [ ] **Signaling HA** — DNS round-robin / load balancer для устранения SPOF
- [ ] **DXGI cascading failure fix** (simple `GC.Collect` перед reinit)

## 🟢 Kernel-level / нетривиальные

- [ ] **WHQL kernel driver** — для Win+L lock screen clicks (как у TeamViewer/AnyDesk). Большой скоуп, требует WHQL сертификации.
- [ ] **IPv6 support** — тестирование + принудительный fallback
- [ ] **Hardware cursor rendering** (WPF HardwareCursor overlay) — возможно уменьшит overhead на viewer side
