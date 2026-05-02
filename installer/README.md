# ZConect installer (InnoSetup)

Production-grade Windows installer that replaces the old "UI self-installs the service
on first run" flow. Puts code in `Program Files`, data in `ProgramData`, registers the
service with idempotent config, and lets the service spawn the UI via
`CreateProcessAsUser` — no UAC prompts after install.

## Prerequisites

- **Windows 10/11** (installer runs on 10.0.17763+)
- **.NET 8 SDK** for publishing (install: `winget install Microsoft.DotNet.SDK.8`)
- **Inno Setup 6** — https://jrsoftware.org/isdl.php (install the Unicode version)
- **PowerShell 7+** recommended for `build.ps1`

End-users of the installer need **.NET 8 Desktop Runtime x64** (framework-dependent
build). The installer checks for it and prompts to download if missing.

## Build

From the repo root:

```powershell
pwsh installer/build.ps1
```

Output: `installer/output/ZConect-Setup-<version>.exe`

Options:
- `-Configuration Debug` — publish Debug instead of Release
- `-Iscc "C:\path\to\ISCC.exe"` — specify ISCC location if autodetect fails
- `-SkipPublish` — re-run ISCC only (reuse existing `staging/`)

## What happens when the installer runs

1. **PrepareToInstall** — if previous version exists, stops and unregisters the service
   via its own `--uninstall` (which polls for `SERVICE_STOPPED`), then kills any running
   `ZConnect.exe` in user sessions.
2. **Files** — copies binaries from `staging/` to `{app}` (default
   `C:\Program Files\ZConect`).
3. **Dirs** — creates `C:\ProgramData\ZConect\` and `C:\ProgramData\ZConect\logs\` with
   `Users: Modify` permissions so both SYSTEM (service) and the logged-in user (UI) can
   write logs/state there.
4. **Run** — calls `ZConectService.exe --install` (idempotent service registration),
   then `sc start ZConectService`. Optional: adds Firewall rule if user checked the
   task.
5. Service starts → detects active user session via `WTSGetActiveConsoleSessionId` →
   spawns `ZConnect.exe --from-service` via `CreateProcessAsUser`. **Installer does
   NOT launch the UI directly** (doing so would run UI as admin — wrong token).
6. Tray icon appears in user session. On the Finish page, the default-checked
   "Launch ZConect" checkbox starts `ZConnect.exe`, which hits the single-instance
   mutex and signals the existing service-spawned UI to show its window.

> **.NET 8 Desktop Runtime** is not auto-detected by the installer (user feedback:
> intrusive dialog). If not installed, the application will fail to start with a
> Windows error pointing to aka.ms/dotnet — users can install the runtime then.

## What happens on uninstall

Order matters: the service must be **fully stopped** before we kill the UI, otherwise
SessionMonitor will respawn a fresh `ZConnect.exe` the moment we kill it.

1. **`ZConectService.exe --stop`** → calls `StopAndWait(15s)` which polls `sc query`
   until `SERVICE_STOPPED`. Now SessionMonitor can no longer spawn the helper.
2. **`taskkill /F /T /IM ZConnect.exe`** → kills lingering UI instances
   (service-spawned AND shortcut-launched). Safe now — service is stopped.
3. **`ZConectService.exe --uninstall`** → `sc delete` to remove from SCM.
4. **`netsh advfirewall firewall delete rule`** → removes firewall rule (silent if absent).
5. **`[UninstallDelete]`** → removes `service.log` + rotated backups.
6. **`CurUninstallStepChanged` prompt** — "Delete all ZConect data?":
   - **No** (default) — keeps `service-config.json` (pairing), `%AppData%\ZConect`
     (UI settings, address book), `%LocalAppData%\ZConect` (logs, IncomingTemp).
     Reinstall restores login/pass instantly.
   - **Yes** — wipes all of the above including `%ProgramData%\ZConect` directory.
7. Inno removes `Program Files\ZConect\*` + `unins000.exe`.

## Where things go

| Path                                              | Purpose                                    | Writable by   |
|---------------------------------------------------|--------------------------------------------|---------------|
| `C:\Program Files\ZConect\*`                      | binaries (read-only after install)         | SYSTEM only   |
| `C:\ProgramData\ZConect\service-config.json`      | service state (DPAPI-encrypted secrets)    | SYSTEM        |
| `C:\ProgramData\ZConect\service.log`              | service rolling log                        | SYSTEM        |
| `%LocalAppData%\ZConect\logs.log`                 | UI rolling log (per-user)                  | current user  |
| `%LocalAppData%\ZConect\crashes.log`              | unhandled exception log                    | current user  |
| `%AppData%\ZConect\client-settings.json`          | UI settings (address book, prefs)          | current user  |

No HKCU/HKLM `Run` keys are written — the service starts the UI automatically on user
login, same pattern as AnyDesk/TeamViewer/RustDesk.

## Silent install

```powershell
ZConect-Setup-1.0.0.exe /VERYSILENT /NORESTART /TASKS="desktopicon,firewallrule"
```

## Testing checklist (on clean VM)

- [ ] Fresh install → `sc query ZConectService` shows `RUNNING`, tray icon appears
- [ ] `C:\ProgramData\ZConect\service.log` has entries
- [ ] `%LocalAppData%\ZConect\logs.log` has entries (after UI runs briefly)
- [ ] Restart VM → service auto-starts → UI appears in tray after login
- [ ] Upgrade install → same login/pass preserved (device_secret in service-config)
- [ ] Uninstall → service gone, ProgramData logs removed, service-config kept unless
      user chose to delete

## Code signing (later)

Add to `[Setup]` in `ZConect.iss`:
```
SignTool=mysigntool
```
And register the signer via `ISCC /Smysigntool="..."` or `iscc -dSignCmd=...`. Not
wired up yet — to be added when an EV cert is available.
