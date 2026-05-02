# Contributing to ZConnect

Thanks for considering a contribution! ZConnect is open source under MIT and welcomes bug reports, feature requests, documentation fixes, and code from anyone.

By participating you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

---

## Quick paths

| I want to… | Do this |
|---|---|
| Report a bug | Open a [GitHub issue](https://github.com/zahar24ru/ZConect/issues/new) — see template below |
| Suggest a feature | Open a [GitHub issue](https://github.com/zahar24ru/ZConect/issues/new) labeled `enhancement` |
| Fix a typo / docs | PR directly — small docs PRs don't need a discussion first |
| Add a feature / fix a bug | Open an issue first to discuss, then PR |
| Report a security vulnerability | Email <zaharovkostia@yandex.ru> — see [SECURITY.md](SECURITY.md). **Do NOT open a public issue.** |
| Translate the UI | See [docs/LOCALIZATION.md](docs/LOCALIZATION.md), pattern is RU + EN side-by-side |

---

## Development setup

### Prerequisites

- **Windows client:** .NET 8 SDK, Visual Studio 2022 / Rider / VS Code with C# extension
- **Server:** Go 1.25+, Docker (for local integration tests)
- **Android:** JDK 21, Android SDK 34, Gradle 9
- **Installer:** [InnoSetup 6.6+](https://jrsoftware.org/isdl.php) (only when you actually want to package)

### Build the client

```powershell
dotnet build client/UiApp/UiApp.sln -c Debug
```

The output appears in `client/UiApp/bin/Debug/net8.0-windows10.0.19041.0/win-x64/`.

To build the installer:

```powershell
powershell -ExecutionPolicy Bypass -File installer/build.ps1
```

### Build the server

```bash
cd server
go build ./...
```

### Run server locally for testing

```bash
cd server
go run ./cmd/signaling
```

The server starts on `:8080` by default. The Windows client looks for the signaling URL in *Settings → System*; point it at `http://127.0.0.1:8080`.

### Build the Android client

```bash
cd android
./gradlew assembleDebug
```

---

## Testing

### .NET unit tests

```powershell
dotnet test client/UiApp/UiApp.sln -c Debug
```

We run **440+ unit tests** across the WPF projects, including [PipeRaceTests](client/ZConect.Tests) (concurrent IPC scenarios), `FileTransfer*` (path traversal, GZip bomb cap), `BootIntegration`, `NetworkResilience`.

Specific test category:

```powershell
dotnet test --filter "Category=Unit"
dotnet test --filter "Category=LiveServer"   # requires a running server
dotnet test --filter "Category=LiveHost"     # requires a running host client
```

### Go server tests

```bash
cd server
go test ./...
```

**76+ Go unit tests** in `internal/{session,admin,bruteforce,ratelimit,turn,realip}`.

### Local integration test pipeline

The script `tools/run_integration_tests.ps1` builds the server, starts it on a random port, runs the C# `LiveServer` tests against it, then cleans up. ~25 sec full cycle.

```powershell
powershell -ExecutionPolicy Bypass -File tools/run_integration_tests.ps1
```

---

## Pull request workflow

1. **Fork** `https://github.com/zahar24ru/ZConect` and create a feature branch off `main`:
   ```bash
   git checkout -b feature/my-feature
   ```
2. **Make focused commits.** Small, atomic commits with clear messages are preferred over one mega-commit. Format:
   ```
   <area>(<scope>): <short summary>

   <optional longer explanation>
   ```
   Examples:
   - `fix(ws): handle reconnect when server returns 423`
   - `feat(android): clipboard sync via DataChannel`
   - `docs(deploy): clarify HTTPS prerequisites`
3. **Run tests** locally before pushing — both `dotnet test` and `go test`.
4. **Localization rule:** any new user-visible UI string MUST be added in both `Strings.resx` (Russian) and `Strings.en-US.resx` (English) in the same commit. See [docs/LOCALIZATION.md](docs/LOCALIZATION.md). The pre-commit grep check is:
   ```bash
   git diff HEAD -- '*.xaml' '*.cs' | grep -E '^\+.*"[А-Яа-яЁё]'
   ```
   should be empty.
5. **Push** to your fork and open a PR against `main`. Reference the related issue (`Closes #123`).
6. **CI** will run on push (when configured). If your PR turns red, fix it and push again — CI re-runs automatically.
7. **Address review feedback** — be patient, the maintainers are volunteers in different timezones.

---

## Code style

- **C#** — standard .NET 8 conventions (PascalCase for types/methods, camelCase for locals, `_camelCase` for private fields). XAML follows MVVM: ViewModels expose `ICommand`, `INotifyPropertyChanged`. Avoid putting logic in code-behind beyond glue code.
- **Go** — `gofmt`'d, `go vet` clean, structured logging via `internal/logging`. Errors wrapped with `fmt.Errorf("...: %w", err)`.
- **Kotlin** — Jetpack Compose stateful, `collectAsStateWithLifecycle`, no GlobalScope. Use the project's logging helpers, not `println`.

---

## Architecture notes for new contributors

- **Three-process Windows design.** Service runs as SYSTEM in session 0 — main pipe server, secrets storage. UI runs in user session — WPF + WebRTC. InputHelper runs under winlogon token — input injection (UAC clicks). They communicate over named pipes with strict ACLs. Read [PIPE_PROTOCOL.md](docs/PIPE_PROTOCOL.md) before touching IPC.
- **WebRTC.** We use Microsoft.MixedReality.WebRTC 2.0.2. It's archived by Microsoft — there's a backlog item to migrate to SIPSorcery (see [ROADMAP.md](docs/ROADMAP.md)). Help wanted.
- **DXGI capture.** GPU-accelerated screen capture with dirty-rects optimization, GDI fallback when DXGI returns AccessLost (e.g., on UAC secure desktop / Win+L lock screen).
- **Server is stateless** for the most part — sessions in memory, telemetry in SQLite. Don't add server-side state without good reason.

---

## Reporting bugs

When opening a bug issue, include:

1. **What happened** vs **what you expected**
2. **Repro steps** — preferably minimal
3. **Environment**: Windows version, .NET version, ZConnect version (Settings → About), server endpoint (community / your own)
4. **Logs** — `%LocalAppData%\ZConect\logs\ui.log` (UI) and `C:\ProgramData\ZConect\logs\service.log` (Service). Redact any session codes / passwords before pasting.
5. **Screenshots** — if it's a UI issue

For security vulnerabilities, **do not** use public issues — email <zaharovkostia@yandex.ru>.

---

## Maintainer contact

- Issues: <https://github.com/zahar24ru/ZConect/issues>
- Telegram: [@zahar24ru](https://t.me/zahar24ru)
- VK community: [vk.com/club237995140](https://vk.com/club237995140)
- Email (security only): <zaharovkostia@yandex.ru>

Thanks for contributing! 🙌
