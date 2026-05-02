# Named Pipe Protocol (внутренний IPC)

Последнее обновление: 2026-04-18.

**Scope:** Internal Windows-only IPC между тремя процессами ZConect-клиента (service + UI + InputHelper). Это НЕ часть внешнего API (тот в [API.md](API.md)) — pipe'ы существуют только локально на одной машине.

## Две трубы

| Pipe | Name | Server | Client |
|---|---|---|---|
| Main | `ZConect_Service_IPC` | Service (`PipeServer`) | UI (`ServicePipeClient`) |
| Helper | `ZConect_Input_Helper_IPC` | InputHelper (`Program.cs`) | Service (`InputHelperManager`) |

**Inversion:** note, что helper pipe — помощник-процесс **хостит** pipe, а service к нему connects. Логика: service spawn'ит helper, знает когда тот готов, и подключается к его pipe. Если helper crash'ится, service детектит process exit и respawn'ит.

## Wire format

Оба pipe'а используют одинаковый протокол:

```
[4 bytes Little-Endian length][UTF-8 JSON payload]
```

- Length prefix — `int32` LE, размер payload в байтах
- Max payload: **1 MB** (hard cap против DoS на малварной стороне)
- Payload invalid JSON / length <= 0 / length > 1 MB → read returns null → pipe closes

## ACL + security

### Main pipe

**PipeSecurity:**
```
InteractiveSid (S-1-5-4)      → ReadWrite     (любой interactive user может подключиться)
LocalSystemSid (S-1-5-18)     → FullControl   (service сам себя, для диагностики)
```

**Post-connect SID verification:**
После первого `ReadAsync` (нужно для установления impersonation context) service делает:
```csharp
pipe.RunAsClient(() => clientSid = WindowsIdentity.GetCurrent().User);
```
И сравнивает с current active console user SID (`WtsActiveUserInfo.TryGetActiveConsoleUserSid`). Если mismatch → pipe закрывается с `pipe_sid_mismatch` warning.

Это предотвращает другого interactive user'а (logged off session, другой аккаунт через Fast User Switching) подключиться к pipe'у активного пользователя.

### Helper pipe

**PipeSecurity:**
```
LocalSystemSid (S-1-5-18) → ReadWrite (только)
```

SYSTEM-only — helper работает под winlogon token (= System integrity), service работает под LocalSystem, оба имеют LocalSystemSid. Любой non-SYSTEM процесс не сможет открыть pipe для read/write.

SID verification на helper side **не делается** — ACL уже restricts. Если SYSTEM-integrity malware на машине — defense depth не поможет (уже compromised).

## Write serialization (_writeLock)

Оба стороны (UI/ServicePipeClient, Service/PipeServer, Service/InputHelperManager) используют `SemaphoreSlim _writeLock` (count=1) вокруг **каждой** пары header+payload writes.

**Почему:** named pipes don't support concurrent writes — два parallel'ных `WriteAsync` могут interleave. Без lock: при 60 Hz mouse move + concurrent config push → header одного message + payload другого → reader parsing error, pipe closed.

**Fix (2026-04-18, C6/C1):** также captured local reference:
```csharp
var pipe = _pipe;                        // snapshot
if (pipe is null || !pipe.IsConnected) return;
await _writeLock.WaitAsync(ct);
try {
    await pipe.WriteAsync(header, ct);   // pipe local, не _pipe
    await pipe.WriteAsync(payload, ct);
    await pipe.FlushAsync(ct);
}
catch (ObjectDisposedException) { /* disposed mid-write — expected race */ }
catch (IOException ex) { /* ... */ }
finally { _writeLock.Release(); }
```

Без captured local — между check и WriteAsync `_pipe` могло измениться (disconnect + reconnect), WriteAsync мог кидать NRE/ObjectDisposedException в caller.

## ⚠️ FlushAsync semantics

`NamedPipeStream.FlushAsync` **ждёт consumption другой стороной**, не просто flush user-mode buffer. Это WriteFile + FlushFileBuffers семантика.

**Последствия:**
- Если reader медленный → writer блокируется на Flush до дренажа
- В тестах без active reader — SendAsync deadlock'ится на Flush

**В PipeRaceTests это решено harness'ом**: server reader запускается в parallel с client ConnectAsync, чтобы hello был consumed когда ConnectAsync делает Flush.

**В production это не проблема** пока reader active и быстр. Potential hardening для будущего — wrap SendAsync в cancellation scope с timeout, чтобы если peer завис → ломать send вместо блокировки forever.

## Main pipe messages

### UI → Service

Все camelCase в wire, JSON.

**`hello`** — при ConnectAsync первое сообщение
```json
{ "type": "hello", "pid": 12345, "sessionId": 4 }
```
Service в ответ шлёт `config` (с небольшим delay если unattended session ещё не создана — ждёт до 5s NotifySessionReady signal).

**`status`** — периодическое обновление состояния
```json
{
  "type": "status",
  "connected": true,
  "currentSessionId": "abc-def-...",
  "loginCode": "12345678",
  "passCode": "87654321"
}
```
Service использует для диагностики / логирования.

**`user_exit`** — user manually закрыл GUI
```json
{ "type": "user_exit" }
```
Service устанавливает `_pipeServer.UserExitRequested = true` → `SessionMonitor` suppress'ит respawn UI пока user снова не откроет.

**`set_uac_policy`** — toggle Secure Desktop policy
```json
{ "type": "set_uac_policy", "inputAction": "disable" }
```
Service пишет `HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\System\PromptOnSecureDesktop` (0 = disable/show UAC на Default desktop, 1 = enable/Winlogon).

**`update_config`** — UI обновляет service config
```json
{
  "type": "update_config",
  "turnUrl": "turn:server:3478",
  "turnUsername": "zconect",
  "turnPassword": "secret",
  "stunUrl": "stun:server:3478"
}
```
Service обновляет `_config`, re-encrypts DPAPI LocalMachine, saves.

**`inject_mouse`** — viewer прислал mouse event
```json
{
  "type": "inject_mouse",
  "inputAction": "move|down|up|click|wheel",
  "inputX": 1234,
  "inputY": 567,
  "inputButton": 1,       // 1=left, 2=right, 3=middle
  "inputDelta": -120      // wheel scroll (MOUSEEVENTF_WHEEL delta)
}
```
Service вызывает `InputHelperManager.ForwardInjectMouseAsync(...)` — пересылает 1-в-1 в helper pipe. Service сам НЕ делает injection.

**`inject_keyboard`**
```json
{
  "type": "inject_keyboard",
  "inputAction": "down|up|press",
  "inputVirtualKey": 0x41,     // 'A'
  "inputScanCode": 0x1E,
  "inputAlt": false,
  "inputCtrl": true,
  "inputShift": true,
  "inputWin": false
}
```
Аналогично forward'ится в helper.

**`inject_ctrl_alt_del`** — единственный inject, обрабатываемый service сам
```json
{ "type": "inject_ctrl_alt_del" }
```
Service dispatch'ит в `InputInjectionAgent.EnqueueCtrlAltDel()` → worker thread в Session 0 вызывает `SendSAS(asUser=false)`. Helper не может вызвать SAS — Windows принимает только от session 0 service process.

### Service → UI

**`config`** — ответ на `hello`, также push'ится при update
```json
{
  "type": "config",
  "signalingUrl": "http://server:8080",
  "webSocketUrl": "ws://server:8080/ws",
  "stunUrl": "stun:server:3478",
  "turnUrl": "turn:server:3478",
  "turnUsername": "zconect",
  "turnPassword": "plaintext-here",       // UI builds ICE server with this
  "machineId": "abc-def-...",
  "deviceSecret": "abc-def-...",
  "unattendedEnabled": true,
  "loginCode": "56484335",                // unattended session codes если enabled
  "passCode": "69160945",
  "currentSessionId": "xyz-...",
  "ownerSecret": "hex-hex-...",
  "wsUrl": "/ws",
  "wsToken": "hmac-..."
}
```
UI applies → builds RTCIceServers, restores unattended codes, skips `POST /session/create` если session уже exists на service side.

**`desktop_changed`** — relay из helper
```json
{ "type": "desktop_changed", "desktop": "Winlogon" }
```
UI обновляет `_cachedActiveDesktop` (volatile), вызывает `OnDesktopSwitched` → force DXGI capture reinit (для tigervnc pattern — re-create Output Duplication on new desktop).

## Helper pipe messages

### Service → Helper

**`inject_mouse`** / **`inject_keyboard`** — те же schemas как на main pipe, просто forward'ятся 1-в-1.

Helper обрабатывает:
1. `SetProcessWindowStation(winsta0)` (один раз на startup)
2. На каждый inject: `OpenInputDesktop(..., GENERIC_ALL)` → текущий active desktop (Default или Winlogon)
3. `SetThreadDesktop(desktop)` — thread переключается на active desktop
4. `SetCursorPos(x, y)` + `SendInput(...)` — injection

Если `SetThreadDesktop` fails → helper skip'ает injection (fix 2026-04-18 batch 1). Если `SendInput` returns 0 → WARN log (input blocked).

### Helper → Service

**`desktop_changed`** — active input desktop switched
```json
{ "type": "desktop_changed", "desktop": "Winlogon|Default" }
```
Helper's `DesktopPollLoopAsync` каждые 500ms вызывает `OpenInputDesktop` + `GetUserObjectInformation(UOI_NAME)` → сравнивает с предыдущим name. При change — push'ит через helper pipe.

Service получает → `PipeServer.PushDesktopChangedAsync` relay'ит в UI через main pipe.

## Routing diagram

```
                    Main pipe                         Helper pipe
 UI (ZConnect.exe)  ◄─────────────►  Service  ◄─────────────►  InputHelper
    user token                    SYSTEM/Session 0              winlogon token
    Medium integrity                                            System integrity

  inject_mouse     →                                          → SendInput
  inject_keyboard  →                 relay only               → SendInput
  inject_ctrl_alt_del  →          InputInjectionAgent (session 0 SAS) — не уходит в helper

  desktop_changed  ←                 relay only               ← poll loop every 500ms

  config           ←  built on hello handshake
  set_uac_policy   →  PromptOnSecureDesktop write to HKLM
  update_config    →  service-config.json update
  hello/status/user_exit  — metadata only
```

## Error handling & disconnect

### Expected race scenarios

- **`ObjectDisposedException` на WriteAsync** — pipe disposed другим thread'ом между check и write. Handled explicitly (2026-04-18 C1/C3/C6 fixes). Logged at DEBUG level.
- **`IOException: Pipe is broken`** — remote end crashed/closed. DEBUG log, caller silently returns.
- **ReadAsync returns null** — clean EOF, peer closed.
- **ReadExactAsync returns partial** — length prefix arrived but payload didn't complete before EOF → null returned → caller breaks loop.

### Reconnect behaviour

**Service's UI pipe** (main):
- `ListenLoopAsync` loop: `WaitForConnectionAsync` → `HandleClientAsync` → при disconnect/error снова `WaitForConnectionAsync`
- 1 instance max — если старый pipe еще не disposed, новый не создастся. Finally block ensures cleanup.
- После user logoff new user's UI может не пройти SID verify — ok, service просто отказывает пока WTS не обновит active console session.

**UI's service pipe** (client):
- `ServicePipeClient.ReadLoopAsync` обнаруживает disconnect → dispose pipe → delay `_currentReconnectDelayMs` (exp backoff 1s→60s cap) → reconnect + `hello`
- Reset backoff к 1s при successful reconnect

**Service's helper pipe** (client):
- `InputHelperManager.ManageLoopAsync` обнаруживает disconnect → reconnect через 1s
- Если helper process exited → spawn заново → reconnect

**Helper's service pipe** (server):
- `PipeServerLoopAsync` — аналогично main, но ACL SYSTEM-only
- 500ms delay в finally block между disconnect and next accept

## Testing

`client/ZConect.Tests/PipeRaceTests.cs` — 7 integration тестов (Category=Integration) покрывают:

1. **Concurrent sends framing** — 50 parallel SendInjectMouseAsync, все доходят как valid JSON → `_writeLock` works
2. **Burst during disconnect** — 60 Hz send loop while server.Dispose() mid-flight → 0 exceptions escape
3. **Send after dispose** — silent return
4. **Reconnect after server restart** — client auto-reconnects to v2, hello delivered, subsequent send works
5. **inject_keyboard round-trip** — все поля сохранены
6. **inject_ctrl_alt_del round-trip** — minimal payload
7. **set_uac_policy serialization** — disable/enable action correct

Harness `PipeHarness.StartAsync` решает FlushAsync deadlock (см. выше).

## History / references

- Initial service pipe (main only): commit `eff08e7` + `879f4fc` (SID check after first ReadAsync)
- Three-process architecture introduced: commit `1405683` (Stage 3 Variant A — helper process)
- Input routing ALL via helper: commit `0c7c6ec`
- Pipe write lock (serialize header+payload): commit `37f6e80`
- 2026-04-18 audit round 1+2 fixes: commits `7d8e358`, `8195eec`
- PipeRaceTests добавлены: commit `7d8e358`

Full audit доков в [SECURITY_AUDIT_2026-04-10.md Addendum](SECURITY_AUDIT_2026-04-10.md#addendum-audit-round-12-2026-04-18).
