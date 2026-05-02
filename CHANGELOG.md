# Changelog

All notable changes to ZConnect are documented here. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.1] — 2026-04-27

### Added

- **Rotating TURN credentials (RFC 7635)** — per-client HMAC short-term creds with default 30-min TTL. Signaling returns `turn_servers` in session create / join / refresh responses. Leaked creds expire automatically. WPF + Android clients both consume rotating creds.
- **HTTPS production deployment** — Caddy reverse proxy with automatic Let's Encrypt provisioning. `deploy/deploy_setup.ps1` accepts `-Domain` and `-AdminEmail` flags. HSTS + secure cookies enabled by default.
- **Public landing page** — `/` serves a static landing with live uptime + status badges (polls `/api/v1/status` every 10 sec).
- **Real client IP via reverse proxy** — new `server/internal/realip` package handles X-Forwarded-For + X-Real-IP correctly behind Caddy. 10 unit tests. Admin panel + bans + audit log + rate limit now see actual client IPs.
- **Admin categorized logs viewer** — 6 chips (Sessions / WebSocket / Auth / Security / Telemetry / Issues) + search + auto-refresh + structured table with color-coded levels.
- **Privacy Policy** — comprehensive 152-FZ-compliant document at `https://zconn.ru/privacy_win.html`. Linked from Settings → About + onboarding step 4 (soft consent checkbox + audit timestamp).
- **Onboarding privacy step** — new 4th step in first-launch wizard with link to Privacy Policy and "I have read" checkbox (non-blocking, audit-trail timestamp `PrivacyPolicyAckedAtUtc`).
- **Android viewer feature parity** — touch gestures fix (double-click, hold-drag, zoom pan), L10n RU/EN (110+ keys), UnattendedAuthCoordinator port (PBKDF2 100k + HMAC-SHA256 password gate), 2-row toolbar, respect device rotation.
- **Android security audit fixes (Variant 1)** — EncryptedSharedPreferences (AES-256-GCM, key in Android Keystore), per-contact saved password (silent reconnect), lockout countdown timer.

### Changed

- **Brand rename**: ZConect → **ZConnect** in all visible UI. Internal technical identifiers (`ZConectService.exe`, `ZConectInputHelper.exe`, pipe names, `%AppData%\ZConect` folder, registry keys) retain the original spelling for backward compatibility.
- **Settings → Application** redesigned — Session / Updates / Security groups, removed confusing `AllowUnattended` checkbox (single source of truth = password set).
- **Onboarding** — 3 steps → 4 steps (added privacy disclosure).
- **Default server** — `https://connect.zconn.ru` (was hardcoded HTTP IP). Legacy URL auto-migration on `Load()`.
- **WebSocket keepalive (server)** — `pingInterval` 30s → 15s for better NAT traversal.

### Fixed

- **30-second WebSocket disconnect** (CRITICAL, 7-iteration debug saga) — root cause: `ClientWebSocket` handles control frames (Ping/Pong) internally and never returns them as `WebSocketReceiveResult`, so `CancelAfter` wrapping `ReceiveAsync` was canceling idle waits. Fix: two-phase receive timeout — first fragment waits unlimited, subsequent fragments armed at 30s anti-slow-loris.
- **Brush cross-thread crash** — `DispatcherUnhandledException` `ArgumentException` when binding non-frozen brushes from background thread. Fix: static frozen brush cache + defensive `Freeze()` in setters.
- **Onboarding repeat after exit** — standalone `svc.Save()` was bypassing `Vm._settings`, later `Vm.SaveSettings()` overwrote `OnboardingCompleted = true` with stale `false`. Fix: in-memory update via `Vm.MarkOnboardingCompleted()`.
- **`AllowUnattended` state lost on host crash/restart** — setter wasn't persisting immediately. Fix: `PersistCurrentSettings()` helper.
- **TURN status: green only after manual refresh** — fast-connect path was ignoring `turn_servers` from refresh response. Fix: `RefreshSession` after fast-connect to fetch creds.
- **mrwebrtc.Dispose() hang** on disconnect → orphaned remote desktop window. Fix: fire `RemoteScreenWindowShouldHide` before dispose + 3 sec timeout.
- **Maintenance gate** wasn't applied to `/join` and `/refresh` (only `/create`).
- **UpdateChecker URL scheme** — added whitelist (`http://`, `https://`) to prevent RCE via `file://` / `javascript:` URIs in `ShellExecute`.
- **`ServiceConfig.Save`** — atomic tempfile + rename (was vulnerable to corruption on power loss).
- **Android `DEFAULT_TURN_PASS = "123456"`** hardcoded password purged.
- **Admin IP whitelist** now supports CIDR notation (was exact-match only).
- **HTTP client timeout** — 30 sec default everywhere (was missing in some places, hung on dead servers).
- **Contact.PresenceBrush** stack overflow when serializing JSON — added `[JsonIgnore]`.
- **Android touch gestures** — jitter threshold 12→30px, double-click → 2× sendTap, hold-drag mode, 2-finger pan when zoomed.
- **Android force landscape** — replaced with `SCREEN_ORIENTATION_USER` to respect device rotation.
- **Android toolbar overflow in portrait** — converted to 2-row layout with `horizontalScroll`.

### Security

- **8 audit fixes** consolidated in `18311a3` — see release notes for detailed list.
- **Legacy URL auto-migration** — old hardcoded HTTP IPs in `client-settings.json` and `service-config.json` auto-rewritten to `https://connect.zconn.ru` on first decrypt after upgrade.

### Toolchain

- AGP 8.13.2 → **9.1.1**, Kotlin 2.1.0 → **2.2.10**, Gradle 9.3.1.

---

## [1.0.0] — 2026-04-22

Initial public release after the deep audit + admin panel + brute-force protection rounds.

### Added

- **Three-process Windows architecture** — Service (SYSTEM, session 0) + UI (user token, WPF) + InputHelper (winlogon token, input injection). UAC clicks work through helper's SYSTEM integrity bypass; Win11 Ctrl+Alt+Del via `SendSAS(asUser=false)`.
- **Admin panel** — 10 tabs (overview, version, health, telemetry, WebRTC sessions, audit, logs, admin sessions, IP bans, password). Session auth (bcrypt cost 12) + CSRF + IP whitelist option + NDJSON audit log.
- **Session brute-force protection** — progressive lockout 15s → 2m → 15m → 1h → permanent BLOCKED. Per-session, not per-IP (NAT-safe).
- **Auto-update notification** — server endpoint `/api/v1/client/version` + client poll (5 sec boot + 6 hr interval). Pulsing badge in header → click opens download URL in default browser.
- **i18n framework** — Russian (primary) + English. ~220 string keys. Installer language picker + GUI Settings dropdown. Installer → GUI seed via `ProgramData/initial-language.txt`.
- **Presence dots** in Address Book + Recent tab — green/red/grey, polled every 30 sec.
- **Quality overlay** in Remote Desktop window — Ctrl+I toggle, FPS/RTT/bitrate.
- **First-run onboarding** — 3-step wizard.
- **Recent connections row** — gradient avatars, save-to-contacts, clear menu.
- **Two-process Windows Service installer** with clean uninstall (taskkill all 3 exes + filesandordirs sweep).

### Initial features (v1.0.0 baseline)

- WebRTC P2P video (VP8) + 4 data channels (control, input, clipboard, file)
- DXGI Output Duplication screen capture (multi-monitor stitching, GDI fallback)
- Adaptive quality ladder (Auto / Extra Low / Low / Medium / High) with hysteresis
- File transfer with SHA-256 verify, GZip compression, conflict resolution, pause/resume
- Bidirectional clipboard sync (text, 256 KB)
- ICE auto-mode (LAN / STUN / TURN), multi-TURN failover, ICE restart on Failed
- Force-relay / disable-TURN toggles in Settings → Connection
- DPAPI-encrypted local secrets, atomic settings write
- 440+ unit tests + LiveServer + LiveHost integration

---

## [Unreleased]

Public release prep — see [ROADMAP.md](docs/ROADMAP.md) for upcoming work.

[1.0.1]: https://github.com/zahar24ru/ZConect/releases/tag/v1.0.1
[1.0.0]: https://github.com/zahar24ru/ZConect/releases/tag/v1.0.0
