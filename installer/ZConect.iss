; ZConect Remote Desktop - InnoSetup installer
; Build: run installer/build.ps1 which publishes .NET projects into staging/ and compiles this script.
; Docs: http://www.jrsoftware.org/ishelp/

#define MyAppName "ZConnect"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "ZConnect"
#define MyAppURL "https://zconn.ru/"
#define MyAppExeName "ZConnect.exe"
; ВАЖНО: имя сервиса оставляем ZConectService (одна N) — это идентификатор в SCM,
; меняется только display name. Если переименовать — сломается upgrade с предыдущей
; версии (старая служба не остановится перед установкой новой).
#define MyServiceName "ZConectService"

[Setup]
AppId={{A7F3ZCNT-9C7E-4F1A-9D3B-5E8C7B2A1F6D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=output
OutputBaseFilename=ZConnect-Setup-{#MyAppVersion}
SetupIconFile=resources\icon.ico
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no
; "dark polar includetitlebar" — forced dark "polar" style, includes dark title bar.
; Requires Inno Setup 6.6.0+.
WizardStyle=dark polar includetitlebar
; Always show the language picker so user can explicitly choose Russian/English
; regardless of Windows UI language (useful for Russian Windows + English-speaking user).
ShowLanguageDialog=yes
DisableDirPage=no
DisableProgramGroupPage=yes
DisableReadyPage=no
DisableWelcomePage=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.FirewallTask=Добавить правило Windows Firewall (для WebRTC/STUN/TURN)
english.FirewallTask=Add Windows Firewall rule (for WebRTC/STUN/TURN)

russian.InstallingService=Регистрация службы ZConect...
english.InstallingService=Registering ZConect service...

russian.StartingService=Запуск службы ZConect...
english.StartingService=Starting ZConect service...

russian.StoppingService=Остановка службы ZConect...
english.StoppingService=Stopping ZConect service...

russian.RemovingService=Удаление службы ZConect...
english.RemovingService=Removing ZConect service...

russian.UninstallWipePrompt=Удалить все данные ZConect (код сопряжения, настройки, логи, адресная книга)?%nНет — сохранить (быстрое восстановление при переустановке).%nДа — полностью очистить ПК от ZConect.
english.UninstallWipePrompt=Remove all ZConect data (pairing code, settings, logs, address book)?%nNo — keep it (fast restore after reinstall).%nYes — completely wipe ZConect from this PC.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "firewallrule"; Description: "{cm:FirewallTask}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Publish output goes here via build.ps1.
Source: "staging\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Default service-config.json — seed только если файла НЕТ (после clean
; uninstall или первая установка). Existing pairing/secrets не перезаписываем.
; Содержит default TURN credentials (demo пароль 123456) чтобы fresh install
; имел работающий relay; код (ServiceConfig defaults) остаётся c пустыми
; значениями — реальные настройки приходят из этого JSON. Пользователь
; может поменять через UI Settings, service сохранит как DPAPI-encrypted.
Source: "default-service-config.json"; DestDir: "{commonappdata}\ZConect"; DestName: "service-config.json"; Flags: onlyifdoesntexist

[Dirs]
; Writable by SYSTEM (service) and BUILTIN\Users (UI runs as user).
Name: "{commonappdata}\ZConect"; Permissions: users-modify
Name: "{commonappdata}\ZConect\logs"; Permissions: users-modify

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 1. Register the service via its own --install command (uses ServiceInstaller.Install
;    which is idempotent — safe to run on upgrade).
Filename: "{app}\{#MyServiceName}.exe"; Parameters: "--install"; Flags: runhidden waituntilterminated; StatusMsg: "{cm:InstallingService}"

; 2. Start the service. The service will detect active user sessions and spawn the UI
;    via CreateProcessAsUser — no need for the installer to start the UI directly.
Filename: "sc.exe"; Parameters: "start {#MyServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "{cm:StartingService}"

; 3. Optional: add inbound firewall rule for the service exe.
Filename: "netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""{#MyAppName} Service"" dir=in action=allow program=""{app}\{#MyServiceName}.exe"" enable=yes"; \
  Flags: runhidden; Tasks: firewallrule

; 4. Post-install launch removed — service auto-spawns the UI via CreateProcessAsUser.

[UninstallRun]
; ORDER MATTERS: stop service first so it can't respawn UI while we're killing it.
;
; 1) Stop service and WAIT for SERVICE_STOPPED. Our CLI --stop calls StopAndWait(15s)
;    which polls "sc query" until state is STOPPED. Once stopped, SessionMonitor can
;    no longer spawn UI.
Filename: "{app}\{#MyServiceName}.exe"; Parameters: "--stop"; Flags: runhidden waituntilterminated; RunOnceId: "StopSvc"

; 2) Kill any lingering UI instances (service-spawned or shortcut-launched).
;    /T also kills child processes. Safe now because service is already stopped.
Filename: "taskkill.exe"; Parameters: "/IM {#MyAppExeName} /F /T"; Flags: runhidden; RunOnceId: "KillUI"

; 2b) Kill InputHelper (spawned by service, same kill-chain).
Filename: "taskkill.exe"; Parameters: "/IM ZConectInputHelper.exe /F /T"; Flags: runhidden; RunOnceId: "KillHelper"

; 3) Unregister the service from SCM.
Filename: "{app}\{#MyServiceName}.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "UninstSvc"

; 4) Force-kill any remaining ZConectService.exe instance. Even after --uninstall
;    exits, Windows may hold file-system cache on the exe/loaded DLLs, causing
;    InnoSetup's upcoming file-delete phase to leave orphaned binaries in {app}.
;    Belt-and-suspenders: taskkill любой оставшийся процесс service'а.
Filename: "taskkill.exe"; Parameters: "/IM {#MyServiceName}.exe /F /T"; Flags: runhidden; RunOnceId: "KillSvc"

; 5) Remove firewall rule (silent if missing).
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#MyAppName} Service"""; Flags: runhidden; RunOnceId: "RmFw"

[UninstallDelete]
; Remove all logs (safe — always regenerated). service-config.json is handled in
; CurUninstallStepChanged with a user prompt so pairing data isn't lost accidentally.
; All logs (service + UI + crashes) are now in C:\ProgramData\ZConect\logs\.
Type: filesandordirs; Name: "{commonappdata}\ZConect\logs"
; initial-language.txt is recreated on re-install, safe to remove.
Type: files; Name: "{commonappdata}\ZConect\initial-language.txt"
; Legacy paths (pre-migration):
Type: files; Name: "{commonappdata}\ZConect\service.log"
Type: files; Name: "{commonappdata}\ZConect\service.*.log"

; Final sweep of install directory. InnoSetup's standard uninstaller only
; removes files it КОНКРЕТНО installed and logged — if a .dll was locked
; briefly при первом проходе, оно осталось orphaned. UninstallDelete runs
; после file-delete phase, когда все handles точно released — gets the
; leftover DLLs (наблюдалось: ZConectService.dll, SessionClient.dll,
; Microsoft.Extensions.*.dll после несколько uninstall циклов). Идемпотентно
; — если директория пуста, ничего не делает.
Type: filesandordirs; Name: "{app}"

[Code]
// ──────────────────────────────────────────────────────────────────────────
// Pre-install: stop + unregister existing service (if any) so we can
// overwrite the exe without "file in use" errors. Uses our --uninstall
// which polls for STOPPED.
// ──────────────────────────────────────────────────────────────────────────
function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  // sc.exe query returns 0 if exists, 1060 if not.
  Result := Exec('sc.exe', 'query {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  ServiceExePath: String;
begin
  Result := '';
  NeedsRestart := False;

  if ServiceExists() then
  begin
    WizardForm.StatusLabel.Caption := ExpandConstant('{cm:StoppingService}');

    // Prefer to use the currently installed service exe to unregister itself
    // (ensures wait-for-stopped logic runs). Fall back to raw sc.exe if missing.
    ServiceExePath := ExpandConstant('{app}\{#MyServiceName}.exe');
    if FileExists(ServiceExePath) then
      Exec(ServiceExePath, '--uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    else
    begin
      Exec('sc.exe', 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(3000); // crude wait for stopped
      Exec('sc.exe', 'delete {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;

    // Also kill any UI instance spawned by the old service so Program Files files aren't locked.
    Exec('taskkill.exe', '/IM {#MyAppExeName} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
  end;
end;

// ──────────────────────────────────────────────────────────────────────────
// Post-install: seed language picked in installer wizard into
// C:\ProgramData\ZConect\initial-language.txt. UI client reads this на
// первом запуске (когда settings.UiLanguage ещё "") и применяет как
// дефолтный язык интерфейса — так выбор в installer переносится в GUI.
// ──────────────────────────────────────────────────────────────────────────
procedure CurStepChanged(CurStep: TSetupStep);
var
  SeedPath: String;
  LangCode: String;
  SeedDir: String;
begin
  if CurStep = ssPostInstall then
  begin
    // ActiveLanguage returns the internal language name from [Languages] —
    // "english" or "russian" в нашем случае.
    if ActiveLanguage = 'english' then
      LangCode := 'en-US'
    else
      LangCode := 'ru-RU';

    SeedDir := ExpandConstant('{commonappdata}\ZConect');
    if not DirExists(SeedDir) then
      ForceDirectories(SeedDir);

    SeedPath := SeedDir + '\initial-language.txt';
    SaveStringToFile(SeedPath, LangCode, False);
  end;
end;

// ──────────────────────────────────────────────────────────────────────────
// Post-uninstall cleanup: ask whether to wipe pairing + per-user data.
// ──────────────────────────────────────────────────────────────────────────
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ConfigFile: String;
  UiDataDir: String;
  UiSettingsDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    ConfigFile := ExpandConstant('{commonappdata}\ZConect\service-config.json');
    UiDataDir := ExpandConstant('{localappdata}\ZConect');    // %LocalAppData%\ZConect — UI logs, crashes, IncomingTemp
    UiSettingsDir := ExpandConstant('{userappdata}\ZConect'); // %AppData%\ZConect — UI settings, address book

    // One combined prompt: keep pairing + user data vs wipe everything.
    // Message is localized via [CustomMessages] -> UninstallWipePrompt.
    if MsgBox(CustomMessage('UninstallWipePrompt'), mbConfirmation, MB_YESNO) = IDYES then
    begin
      // Delete service config (pairing)
      if FileExists(ConfigFile) then DeleteFile(ConfigFile);

      // Delete service data dir if empty (logs were removed by [UninstallDelete])
      DelTree(ExpandConstant('{commonappdata}\ZConect'), True, True, True);

      // Delete UI per-user data (logs.log, crashes.log, IncomingTemp, client-settings.json).
      // Note: only affects the user running the uninstaller; other users' data stays.
      if DirExists(UiDataDir) then
        DelTree(UiDataDir, True, True, True);
      if DirExists(UiSettingsDir) then
        DelTree(UiSettingsDir, True, True, True);
    end;
  end;
end;
