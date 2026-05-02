# Localization (i18n) — ru-RU / en-US

Last updated: 2026-04-28.

ZConnect UI полностью локализован на русский и английский. Русский — primary
(neutral) язык, английский — satellite assembly (`en-US/ZConnect.resources.dll`).

**Android viewer** также локализован (Phase 1, 2026-04-27): 110+ keys в
`values/strings.xml` (English) + `values-ru/strings.xml` (Russian). Подробности
ниже в секции «Android L10n».

## ⚠️ Rule: new UI strings MUST be added in both languages immediately

Любая новая строка, видимая пользователю (button label, TextBlock, ToolTip, MessageBox,
toast, status text, confirm dialog), добавляется в **обе** локали одним коммитом.
Частично локализованные коммиты запрещены — в production получается смесь русского
и английского при переключении языка.

**Pre-commit check** (копируй в CI hook или memorize):

```bash
git diff HEAD -- '*.xaml' '*.cs' \
  | grep -E '^\+.*"[А-Яа-яЁё]' \
  | grep -v -E '^\+.*(/{2,3}|<summary>|<!--)'
```

Должно быть пусто. Если что-то нашлось — либо локализуй через `Strings.*`, либо
явно отметь comment что это debug-only / log-only string.

## Adding a new localized string — checklist

1. **Придумай ключ** в snake/Pascal-case формате `Section_Purpose[_Format]`:
   - `Settings_CheckUpdate_Button`
   - `Status_ConnectionLost`
   - `Dialog_Delete_Confirm_Format` (суффикс `_Format` = содержит `{0}`, `{1}` placeholders)

2. **Добавь в `client/UiApp/Properties/Strings.resx`** (ru-RU primary):
   ```xml
   <data name="MyKey" xml:space="preserve"><value>Русский текст</value></data>
   ```

3. **Добавь параллельный entry в `client/UiApp/Properties/Strings.en-US.resx`**:
   ```xml
   <data name="MyKey" xml:space="preserve"><value>English text</value></data>
   ```

4. **Добавь property в `client/UiApp/Properties/Strings.Designer.cs`** (hand-maintained):
   ```csharp
   public static string MyKey => Get();
   ```

5. **Используй в XAML**:
   ```xml
   <Window ... xmlns:props="clr-namespace:UiApp.Properties">
     <Button Content="{x:Static props:Strings.MyKey}"/>
   </Window>
   ```

6. **Используй в code** (добавь `using UiApp.Properties;`):
   ```csharp
   StatusText = Strings.MyKey;
   var msg = string.Format(Strings.MyKey_Format, arg1, arg2);
   ```

## Why Designer.cs is hand-maintained

.NET 8 SDK auto-generation через `resgen` даёт duplicate `EmbeddedResource`
warnings в WPF `*_wpftmp` targets (проблема с implicit SDK include). Ручной
wrapper с `[CallerMemberName]` + `ResourceManager.GetString(name, Culture)`
проще и predictable.

## Runtime flow

**On startup (`App.OnStartup`):**

1. `Services.SettingsService().Load()` → читает `%AppData%\ZConect\client-settings.json`.
2. Если `settings.UiLanguage` пустой (первый запуск) → smart fallback:
   - Читает `C:\ProgramData\ZConect\initial-language.txt` (seed от installer'а, см. ниже).
   - Валидирует код (`ru-RU` или `en-US`) → сохраняет в `ClientSettings`.
3. Применяет культуру:
   ```csharp
   var ci = new CultureInfo(settings.UiLanguage);
   CultureInfo.DefaultThreadCurrentUICulture = ci;
   Thread.CurrentThread.CurrentUICulture = ci;
   Strings.Culture = ci;
   ```
4. Затем Window init — `{x:Static props:Strings.*}` lookup'ы резолвятся через
   `ResourceManager.GetString(name, Strings.Culture)`.

**User change in Settings → Язык:**

1. `LanguageCombo_SelectionChanged` → `Vm.UiLanguage = code; Vm.SaveSettings()`.
2. MessageBox "Язык сохранён. Перезапустите приложение" (на ТОМ языке который user выбрал).
3. User делает Exit + re-launch → новая культура применяется на startup.

**Почему нельзя hot-swap без restart** — WPF `ResourceManager` резолвит `{x:Static}`
binding при создании control'а и кэширует string. Чтобы применить смену culture
нужно re-create весь window tree, что проще через полный restart process'а.

## Installer language picker → GUI default

InnoSetup installer показывает диалог выбора языка (Russian / English) при старте
через `ShowLanguageDialog=yes` в `[Setup]`. `[Languages]`:

```
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
```

После install `[Code]/CurStepChanged(ssPostInstall)` пишет seed-файл:

```pascal
C:\ProgramData\ZConect\initial-language.txt = "ru-RU" или "en-US"
```

(на основе `ActiveLanguage` — имя из `[Languages]`).

App на первом запуске (когда `settings.UiLanguage` пустой) читает seed и применяет
как default. Seed-файл **не удаляется** при startup — новые Windows user account'ы
на той же машине получат тот же дефолт до явного изменения через Настройки.

Uninstaller чистит seed через `[UninstallDelete]`.

## Custom messages в installer

Для installer-specific строк (service install progress, firewall task, uninstall
wipe prompt) используется `[CustomMessages]`:

```ini
russian.FirewallTask=Добавить правило Windows Firewall (для WebRTC/STUN/TURN)
english.FirewallTask=Add Windows Firewall rule (for WebRTC/STUN/TURN)

russian.UninstallWipePrompt=Удалить все данные ZConnect...
english.UninstallWipePrompt=Remove all ZConnect data...
```

Access в Pascal code: `CustomMessage('UninstallWipePrompt')` или в `[Run]`/`[Tasks]`
через `{cm:FirewallTask}`.

Встроенные wizard-pages (Welcome/License/Install/Ready/Finished) автоматически
используют соответствующий `.isl` (Russian.isl или Default.isl).

## File inventory

```
client/UiApp/
  Properties/
    Strings.resx              — ru-RU primary
    Strings.en-US.resx        — en-US satellite
    Strings.Designer.cs       — wrapper, hand-maintained
  App.xaml.cs                 — OnStartup culture apply + seed read
  UiApp.csproj                — <NeutralLanguage>ru-RU</NeutralLanguage>

installer/
  ZConect.iss                 — [Languages]/[CustomMessages]/CurStepChanged seed write
```

## Known remaining hardcoded strings (deferred)

Менее user-facing / specialized (~50 strings), запланировано для следующего раунда
l10n cleanup:

- `ViewModels/FileTransferWindowViewModel.cs` — workflow messages (создание папки, удаление, переименование на remote стороне).
- `ViewModels/MainViewModel.Uac/.FileTransfer/.Video.cs` — specialized status strings.
- `Services/WsReconnectionManager.cs` — "Переподключение (N/M)..."
- `Models/Contact.cs`, `RecentItem.cs` — relative time ("только что", "вчера").
- `Models/TransferDisplayItem.cs`, `TransferQueueProgress.cs` — transfer queue status.
- `FileTransferWindow.xaml.cs` — new folder prompt, delete confirm, file-exists conflict resolver.
- `App.xaml.cs` — audio 192kHz incompatibility startup MessageBox (edge case).

Search все оставшиеся:

```bash
grep -rn '"[А-Яа-яЁё][^"]*"' client/UiApp --include='*.cs' --include='*.xaml' \
  | grep -v -E 'bin/|obj/|// |/// |<summary>|<!--'
```

## Testing

Fresh install → English → GUI open → все UI на английском (включая tray menu, toasts,
Help (?), Settings все 4 таба, Remote Desktop toolbar, File Transfer panel, все Dialogs,
onboarding wizard).

Change language в Settings → Restart → тот же tour повторить.

## Android L10n (Phase 1, DONE 2026-04-27)

Android viewer использует стандартный Android Resources mechanism — никакого
custom infrastructure'а. Locale выбирается **автоматически** на основе системной
языковой настройки устройства (Android `Locale`).

### File layout

```
android/app/src/main/res/
  values/strings.xml           — default (English)
  values-ru/strings.xml        — Russian (когда device locale = ru)
```

### Conventions

- Ключи: snake_case в `<string name="...">` (Android style)
- Format strings: `<string name="format_key">Привет %1$s, у вас %2$d сообщений</string>`
- Compose access: `stringResource(R.string.key_name)` или `stringResource(R.string.format_key, arg1, arg2)`
- В data layer (suspend funcs / non-Composable): inject `Context` и используй `context.getString(R.string.key)`

### Coverage (Phase 1, commit `ad89c07`)

110+ keys покрывают 4 главных screen'а:

- **ConnectScreen** — login/password fields, server status, Recent row, Connect button, error toasts
- **AddressBookScreen** — search field, sort/filter chips, contact actions, delete confirm dialog, empty state
- **RemoteScreen** — toolbar actions (zoom, screen list, gestures help, disconnect), connection status, GestureHelpDialog
- **ContactForm** — labels, save/cancel buttons, validation messages

Plus:

- 8 unattended_* keys для UnattendedPasswordDialog (Variant A port, commit `ca3dfc4`)
- `unattended_dialog_remember` для "Remember password" checkbox (Variant 1 audit fix, commit `ba33806`)
- `unattended_locked_out_format` для lockout countdown timer

### Pending (Phase 2)

- ~10-15 системных строк (logcat tags, debug log messages — НЕ user-visible)
- Toast strings в WebRtcSignaling/WebRtcViewerEngine — частично hardcoded English (acceptable for now: техническая аудитория)
- Settings screen (когда будет добавлен в v3)

### Pre-commit check для Android

Аналог WPF rule — новые user-visible strings в обе локали одним коммитом.
Проверка через grep:

```bash
git diff HEAD -- 'android/app/src/main/java/**/*.kt' \
  | grep -E '^\+.*"[А-Яа-яЁё]' \
  | grep -v -E '^\+.*(/{2,3}|\*|//)'
```

Если что-то нашлось — extract в `R.string.*` через `stringResource(...)`.
