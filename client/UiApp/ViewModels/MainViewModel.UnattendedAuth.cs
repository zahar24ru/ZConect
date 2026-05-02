using UiApp.Models;
using UiApp.Services;
using WebRtcTransport;

namespace UiApp.ViewModels;

/// <summary>
/// Unattended password gate — опциональный pre-offer auth handshake.
/// Host сохраняет локальный пароль; viewer при connect может его ввести чтобы
/// пропустить стандартный confirmation dialog. Lockout + progressive timeout
/// реализуются в <see cref="UnattendedAuthService"/>, wire-protocol — в
/// <see cref="UnattendedAuthCoordinator"/>.
/// </summary>
public sealed partial class MainViewModel
{
    private UnattendedAuthService? _unattendedAuthService;
    private UnattendedAuthCoordinator? _unattendedAuthCoord;
    /// <summary>Nonce сгенерированный host'ом при последнем probe. One-shot — clear after verify.</summary>
    private byte[]? _pendingAuthNonce;
    /// <summary>Timestamp когда viewer пришёл с probe — для latency-between-probe-and-password в audit лог.</summary>
    private DateTime? _lastProbeUtc;
    /// <summary>True если текущая сессия была auto-approved через password. Используется в
    /// OnIceStateConnected чтобы дополнительно залогировать remote IP viewer'а (при relay —
    /// TURN IP, но даёт endpoint для пост-hoc анализа).</summary>
    private bool _wasAutoApprovedViaPassword;

    /// <summary>Контакт к которому сейчас подключается viewer (если connect через addressbook).
    /// null при ad-hoc connect по кодам. Используется для auto-fill saved password и сохранения.</summary>
    private UiApp.Models.Contact? _currentUnattendedContact;
    /// <summary>User отметил «Запомнить пароль» в dialog'е → сохранить в контакт после ok.</summary>
    private bool _rememberPasswordRequested;
    /// <summary>Pending password из dialog'а для сохранения в контакт на success.</summary>
    private string? _pendingPasswordToRemember;

    /// <summary>True если host задал local password для auto-approve. UI'ский флаг для Settings.</summary>
    public bool IsUnattendedPasswordSet =>
        !string.IsNullOrEmpty(_settings.UnattendedPasswordHash) &&
        !string.IsNullOrEmpty(_settings.UnattendedPasswordSalt);

    /// <summary>Settings UI — задать / сменить password. Throws ArgumentException если невалиден.
    /// Автоматически включает AllowUnattended — без этого флага password бесполезен (probe вернёт
    /// mode=confirmation и viewer не увидит password prompt).</summary>
    public void SetUnattendedPassword(string plaintext)
    {
        var svc = GetOrCreateUnattendedServiceForSettings();
        svc.SetPassword(plaintext);
        _settings.AllowUnattended = true;
        _settingsService.Save(_settings);
        OnPropertyChanged(nameof(IsUnattendedPasswordSet));
        OnPropertyChanged(nameof(AllowUnattended));
        OnPropertyChanged(nameof(HasUnattendedLockout));
        OnPropertyChanged(nameof(UnattendedLockoutDescription));
    }

    /// <summary>True если сейчас активен lockout (tier > 0 или until в будущем).
    /// UI использует для показа/скрытия «Сбросить блокировку» кнопки + красного warning-баннера
    /// в MainWindow.</summary>
    public bool HasUnattendedLockout =>
        _settings.UnattendedLockoutTier > 0
        || _settings.UnattendedLockoutUntilUtc > DateTime.UtcNow
        || _settings.UnattendedFailedAttempts > 0;

    /// <summary>Человекочитаемое описание состояния блокировки — для красного текста в
    /// Settings (LockoutWarningText) и MainWindow top banner. Пусто если нет блокировки.</summary>
    public string UnattendedLockoutDescription
    {
        get
        {
            var now = DateTime.UtcNow;
            if (_settings.UnattendedLockoutUntilUtc > now)
            {
                var remaining = _settings.UnattendedLockoutUntilUtc - now;
                var totalMin = (int)remaining.TotalMinutes;
                var sec = remaining.Seconds;
                return $"🔒 Заблокировано: {totalMin}:{sec:D2} (tier {_settings.UnattendedLockoutTier})";
            }
            if (_settings.UnattendedLockoutTier > 0)
                return $"⚠ Блокировка снята. Следующая серия неверных попыток заблокирует дольше (tier {_settings.UnattendedLockoutTier}).";
            if (_settings.UnattendedFailedAttempts > 0)
                return $"⚠ Неверных попыток: {_settings.UnattendedFailedAttempts}";
            return string.Empty;
        }
    }

    /// <summary>Settings UI — сбросить lockout state (counter/tier/until). Password не трогаем.</summary>
    public void ResetUnattendedLockout()
    {
        var svc = GetOrCreateUnattendedServiceForSettings();
        svc.ResetLockout();
        OnPropertyChanged(nameof(HasUnattendedLockout));
        OnPropertyChanged(nameof(UnattendedLockoutDescription));
    }

    /// <summary>Settings UI — удалить password. Автоматически выключает AllowUnattended.</summary>
    public void ClearUnattendedPassword()
    {
        var svc = GetOrCreateUnattendedServiceForSettings();
        svc.ClearPassword();
        _settings.AllowUnattended = false;
        _settingsService.Save(_settings);
        OnPropertyChanged(nameof(IsUnattendedPasswordSet));
        OnPropertyChanged(nameof(AllowUnattended));
        OnPropertyChanged(nameof(HasUnattendedLockout));
        OnPropertyChanged(nameof(UnattendedLockoutDescription));
    }

    /// <summary>
    /// В connect-flow UnattendedAuthService создаётся в InitializeUnattendedAuth.
    /// Но Settings window может быть открыт до любого connect — создаём adhoc для management.
    /// </summary>
    private UnattendedAuthService GetOrCreateUnattendedServiceForSettings()
    {
        return _unattendedAuthService ?? new UnattendedAuthService(
            _settings,
            persist: () => _settingsService.Save(_settings),
            log: (module, evt) => _logService.Info(module, evt));
    }

    /// <summary>
    /// Вызывается из CleanupConnectionAsync и/или перед созданием нового coord'а.
    /// Отписывает handlers и обнуляет state — UI не должен тащить dangling subscribers в следующую сессию.
    /// </summary>
    private void DisposeUnattendedAuthCoord()
    {
        if (_unattendedAuthCoord is null) return;
        _unattendedAuthCoord.ViewerProbeReceived -= OnUnattendedViewerProbeReceived;
        _unattendedAuthCoord.ViewerPasswordReceived -= OnUnattendedViewerPasswordReceived;
        _unattendedAuthCoord.ViewerSkippedPassword -= OnUnattendedViewerSkippedPassword;
        try { _unattendedAuthCoord.Dispose(); } catch { /* silent */ }
        _unattendedAuthCoord = null;
        _pendingAuthNonce = null;
        _lastProbeUtc = null;
        _wasAutoApprovedViaPassword = false;
        _passwordApprovalEndpointLogged = false;
    }

    /// <summary>
    /// Создать service + coordinator, подписать host events. Вызывается в ConnectWsAsync
    /// после SignalingCoordinator и DataChannelCoordinator, до _signalingClient.ConnectAsync.
    /// </summary>
    private void InitializeUnattendedAuth(string sessionId)
    {
        DisposeUnattendedAuthCoord(); // idempotent

        _unattendedAuthService = new UnattendedAuthService(
            _settings,
            persist: () => _settingsService.Save(_settings),
            log: (module, evt) => _logService.Info(module, evt));

        _unattendedAuthCoord = new UnattendedAuthCoordinator(
            _signalingClient!,
            msg => _logService.Debug("UnattendedAuth", msg));
        _unattendedAuthCoord.SetSession(sessionId);

        // Diagnostic 2026-04-27: трекать что handlers подписаны для host'а.
        _logService.Debug("UnattendedAuth", $"init_unattended role={_role} session={sessionId.Substring(0, Math.Min(8, sessionId.Length))} svc_configured={_unattendedAuthService.IsConfigured}");

        if (_role == ConnectionRole.Host)
        {
            _unattendedAuthCoord.ViewerProbeReceived += OnUnattendedViewerProbeReceived;
            _unattendedAuthCoord.ViewerPasswordReceived += OnUnattendedViewerPasswordReceived;
            _unattendedAuthCoord.ViewerSkippedPassword += OnUnattendedViewerSkippedPassword;
            _logService.Debug("UnattendedAuth", "host_handlers_subscribed");
        }
    }

    // ── Host-side handlers ─────────────────────────────────────────────────

    private async void OnUnattendedViewerProbeReceived()
    {
        // Diagnostic 2026-04-27: bug report shows probe arrives на host'е (ws_message_*
        // logged) но handler возможно не вызывается → нет probe_responded log.
        _logService.Debug("UnattendedAuth", $"probe_handler_entered role={_role} coord_null={_unattendedAuthCoord is null} svc_null={_unattendedAuthService is null}");
        if (_role != ConnectionRole.Host || _unattendedAuthCoord is null || _unattendedAuthService is null)
        {
            _logService.Warn("UnattendedAuth", "probe_handler_early_return_state_mismatch");
            return;
        }
        try
        {
            // Single source of truth (UX fix 2026-04-26): если password configured —
            // host принимает viewer'ов с password proof. AllowUnattended legacy flag,
            // оставлен для backward-compat но больше не gate'ит решение здесь.
            // Раньше: user случайно снимал checkbox AllowUnattended → password set но
            // gate отключён → host требовал confirmation, что путало пользователя.
            // Теперь: password set ⇒ password gate active. Чтобы выключить — удалить
            // password (кнопкой Удалить в Settings → Security).
            if (_unattendedAuthService.IsConfigured)
            {
                var nonce = UnattendedAuthService.GenerateNonce();
                _pendingAuthNonce = nonce;
                _lastProbeUtc = DateTime.UtcNow;  // R3: для audit log latency metric
                var saltB64 = _unattendedAuthService.SaltBase64 ?? "";
                var nonceB64 = Convert.ToBase64String(nonce);
                await _unattendedAuthCoord.SendModePasswordAsync(saltB64, nonceB64, _lifetimeCts.Token);
                _logService.Info("UnattendedAuth", $"probe_responded_mode_password session_id={_currentSessionId}");
            }
            else
            {
                await _unattendedAuthCoord.SendModeConfirmationOnlyAsync(_lifetimeCts.Token);
                _logService.Info("UnattendedAuth", "probe_responded_mode_confirmation");
            }
        }
        catch (Exception ex)
        {
            _logService.Warn("UnattendedAuth", $"probe_response_failed: {ex.Message}");
        }
    }

    private async void OnUnattendedViewerPasswordReceived(string proofBase64)
    {
        if (_role != ConnectionRole.Host || _unattendedAuthCoord is null || _unattendedAuthService is null) return;
        var nonce = _pendingAuthNonce;

        if (nonce is null)
        {
            // Виewer шлёт password без предшествующего probe (stale/malicious). Отказываем,
            // counter НЕ инкрементируем (нет nonce чтобы verify). Viewer должен заново
            // пройти probe → получить новый nonce → тогда counter работает.
            _logService.Warn("UnattendedAuth", "password_received_without_pending_nonce_rejected");
            try { await _unattendedAuthCoord.SendResultAsync(ok: false, reason: "no_challenge", ct: _lifetimeCts.Token); }
            catch { /* silent */ }
            return;
        }

        try
        {
            var proof = Convert.FromBase64String(proofBase64);
            var outcome = _unattendedAuthService.VerifyProof(proof, nonce);

            // R1 side-effect: VerifyProof мутирует failed_attempts/tier/until в _settings —
            // MainWindow banner / Settings warning binding'и должны обновиться сразу.
            _uiContext.Post(_ =>
            {
                OnPropertyChanged(nameof(HasUnattendedLockout));
                OnPropertyChanged(nameof(UnattendedLockoutDescription));
            }, null);

            switch (outcome.Result)
            {
                case UnattendedAuthResult.Ok:
                    _pendingAuthNonce = null; // one-shot — consumed by success (replay protection)
                    _viewerApproved = true;  // bypass confirmation gate (line 679 in Connection.cs)
                    _wasAutoApprovedViaPassword = true;  // R3: ICE handler дополнительно залогирует IP
                    // R3: enriched audit log — session ID + wall-clock time + latency от probe
                    // + attempts до success (0 если сразу, >0 если были неверные попытки).
                    var latencyMs = _lastProbeUtc.HasValue
                        ? (long)(DateTime.UtcNow - _lastProbeUtc.Value).TotalMilliseconds
                        : -1;
                    _logService.Info("UnattendedAuth",
                        $"viewer_auto_approved_via_password session_id={_currentSessionId} " +
                        $"utc={DateTime.UtcNow:O} probe_to_ok_ms={latencyMs} " +
                        $"prior_attempts={_settings.UnattendedFailedAttempts}");
                    await _unattendedAuthCoord.SendResultAsync(ok: true, ct: _lifetimeCts.Token);
                    // User awareness: на host'е показываем toast + звук что viewer подключился по паролю.
                    // Иначе host не узнает — подключение silent, что создаёт security blind spot.
                    // NotifyViewerFullyApproved с custom синим статусом отличает password-path
                    // от green confirmation-approve. Idempotent — следующий beforeCreateAnswerAsync
                    // не будет дубликатом тоста.
                    try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    // Crash fix 2026-04-26: Brush создаётся на background thread (signaling
                    // message handler), потом передаётся через _uiContext.Post в DependencyProperty
                    // setter где binding tries to attach → cross-thread ArgumentException
                    // ("DependencySource в том же потоке"). .Freeze() makes Brush immutable +
                    // thread-safe — binding может attach safely.
                    var blueBrush = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(96, 165, 250));
                    blueBrush.Freeze();
                    NotifyViewerFullyApproved(
                        customStatus: "Viewer подключён (пароль)",
                        customBrush: blueBrush); // blue
                    break;
                case UnattendedAuthResult.WrongPassword:
                    // R1 (replay defence): очищаем nonce сразу после wrong-verify. Любой
                    // последующий proof (legit retry ИЛИ attacker replay из WS sniff'а)
                    // попадёт в ветку "no_challenge" → counter НЕ растёт для replay'ов.
                    //
                    // Legit viewer при этом не ломается: coordinator ловит no_challenge →
                    // silent re-probe (уже существующая логика) → host генерирует свежий
                    // nonce → viewer пересчитывает proof с новым nonce → counter++ на
                    // каждой настоящей попытке. Rate-limit через tier lockout работает
                    // как раньше (3 fails = 1 min, и т.д.).
                    _pendingAuthNonce = null;
                    await _unattendedAuthCoord.SendResultAsync(
                        ok: false,
                        reason: "wrong_password",
                        attemptsLeft: outcome.AttemptsLeft,
                        lockoutTier: outcome.LockoutTier,
                        ct: _lifetimeCts.Token);
                    break;
                case UnattendedAuthResult.LockedOut:
                    _pendingAuthNonce = null; // locked — viewer должен пройти новый probe после unlock
                    await _unattendedAuthCoord.SendResultAsync(
                        ok: false,
                        reason: "locked_out",
                        lockoutRemainingSec: (int)outcome.LockoutRemaining.TotalSeconds,
                        lockoutTier: outcome.LockoutTier,
                        ct: _lifetimeCts.Token);
                    break;
                default:
                    _pendingAuthNonce = null;
                    await _unattendedAuthCoord.SendResultAsync(ok: false, reason: "not_configured", ct: _lifetimeCts.Token);
                    break;
            }
        }
        catch (FormatException ex)
        {
            _pendingAuthNonce = null;
            _logService.Warn("UnattendedAuth", $"password_proof_malformed: {ex.Message}");
            try { await _unattendedAuthCoord.SendResultAsync(ok: false, reason: "malformed_proof", ct: _lifetimeCts.Token); }
            catch { /* silent */ }
        }
        catch (Exception ex)
        {
            _logService.Error("UnattendedAuth", "password_verify_failed", ex.Message);
        }
    }

    private void OnUnattendedViewerSkippedPassword()
    {
        if (_role != ConnectionRole.Host) return;
        _pendingAuthNonce = null;
        _logService.Info("UnattendedAuth", "viewer_skipped_password_will_use_confirmation");
        // No action — нормальный offer→answer→confirmation flow сработает.
    }

    // ── Viewer-side handshake ──────────────────────────────────────────────

    /// <summary>Caller из ConnectToContactAsync: задаёт контакт для auto-fill saved password'а.</summary>
    internal void SetCurrentUnattendedContact(UiApp.Models.Contact? contact)
    {
        _currentUnattendedContact = contact;
        _rememberPasswordRequested = false;
        _pendingPasswordToRemember = null;
    }

    /// <summary>
    /// Viewer: pre-offer handshake. Возвращает true если можно идти дальше на offer,
    /// false — fatal (locked out / cancelled → connect abort).
    /// </summary>
    private async Task<bool> RunViewerUnattendedHandshakeAsync(CancellationToken ct)
    {
        if (_role != ConnectionRole.Viewer || _unattendedAuthCoord is null || _unattendedAuthService is null)
            return true;

        try
        {
            var outcome = await _unattendedAuthCoord.RunViewerHandshakeAsync(
                passwordProvider: RequestPasswordFromUserAsync,
                computeProof: (pwd, salt, nonce) => _unattendedAuthService!.ComputeProof(pwd, salt, nonce),
                probeTimeoutMs: 5000,
                ct: ct);

            if (outcome.IsLockedOut)
            {
                var min = Math.Max(1, outcome.LockoutRemainingSec / 60);
                SetStatus($"Подключение заблокировано на ~{min} мин (слишком много неверных попыток)", ConnectionState.Error);
                _logService.Warn("UnattendedAuth", $"viewer_locked_out sec={outcome.LockoutRemainingSec} tier={outcome.LockoutTier}");
                return false;
            }

            // On Approved: if user checked «Remember» — persist password in contact.
            if (outcome.IsApproved && _rememberPasswordRequested && _currentUnattendedContact is not null
                && !string.IsNullOrEmpty(_pendingPasswordToRemember))
            {
                _currentUnattendedContact.SavedUnattendedPassword = _pendingPasswordToRemember;
                try
                {
                    _addressBookService.Save(Contacts.ToList());
                    _logService.Info("UnattendedAuth", $"password_remembered_for_contact={_currentUnattendedContact.Name}");
                }
                catch (Exception ex)
                {
                    _logService.Warn("UnattendedAuth", $"password_remember_save_failed: {ex.Message}");
                }
            }

            _rememberPasswordRequested = false;
            _pendingPasswordToRemember = null;

            // Approved (auto-skip confirmation on host) OR FallbackToConfirmation (host покажет dialog)
            // OR Cancelled (но мы отсюда не выходим по Cancel — caller обработает через ct).
            // Во всех валидных случаях — продолжаем с offer.
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // R2: fail-closed. Раньше был `return true` (fallback к confirmation),
            // но при этом host, у которого user включил password gate, мог получить
            // viewer'а в обход password'а через unexpected exception. Теперь — abort.
            // Viewer может retry явно; host никогда не auto-approve'нет без password.
            _logService.Warn("UnattendedAuth", $"viewer_handshake_aborted_fail_closed: {ex.Message}");
            SetStatus("Сбой unattended-аутентификации — подключение отменено", ConnectionState.Error);
            return false;
        }
    }

    /// <summary>
    /// UI-hook для viewer password prompt. Возвращает:
    ///   не-null password → viewer шлёт proof;
    ///   null → viewer шлёт skip → host показывает confirmation dialog.
    ///
    /// Первый attempt (не retry, не locked): если в контакте есть SavedUnattendedPassword —
    /// возвращаем silently без dialog'а. Saved password может быть stale (host сменил) —
    /// тогда host ответит wrong_password → следующий вызов уже показывает dialog с retry hint.
    /// </summary>
    private Task<string?> RequestPasswordFromUserAsync(ViewerAuthPrompt prompt)
    {
        _logService.Debug("UnattendedAuth", $"viewer_prompt is_retry={prompt.IsRetry} attempts_left={prompt.AttemptsLeft} locked={prompt.IsLockedOut} reason={prompt.LastReason ?? "none"}");

        // Fast path — saved password для контакта.
        if (!prompt.IsRetry && !prompt.IsLockedOut
            && _currentUnattendedContact is not null
            && !string.IsNullOrEmpty(_currentUnattendedContact.SavedUnattendedPassword))
        {
            _logService.Info("UnattendedAuth", $"viewer_using_saved_password_for_contact={_currentUnattendedContact.Name}");
            return Task.FromResult<string?>(_currentUnattendedContact.SavedUnattendedPassword);
        }

        // Если это retry ПОСЛЕ провала saved password — очистим его из контакта (stale).
        if (prompt.IsRetry && prompt.LastReason == "wrong_password"
            && _currentUnattendedContact is not null
            && !string.IsNullOrEmpty(_currentUnattendedContact.SavedUnattendedPassword))
        {
            _logService.Info("UnattendedAuth", "saved_password_rejected_clearing_from_contact");
            _currentUnattendedContact.SavedUnattendedPassword = string.Empty;
            // Diagnostic logging: если DPAPI/disk fail при стирании stale password —
            // на следующем connect снова попробуем (и снова получим wrong_password reject).
            // User увидит retry dialog. Знание про exception помогает при debugging.
            try { _addressBookService.Save(Contacts.ToList()); }
            catch (Exception ex) { _logService.Warn("UnattendedAuth", $"clear_stale_password_save_failed: {ex.Message}"); }
        }

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _uiContext.Post(_ =>
        {
            var mainWindow = System.Windows.Application.Current?.MainWindow as MainWindow;
            try
            {
                var canRemember = _currentUnattendedContact is not null; // показываем checkbox только если контакт известен
                var dlg = new UiApp.Dialogs.UnattendedPasswordDialog(
                    isRetry: prompt.IsRetry,
                    attemptsLeft: prompt.AttemptsLeft,
                    lastReason: prompt.LastReason,
                    isLockedOut: prompt.IsLockedOut,
                    lockoutRemainingSec: prompt.LockoutRemainingSec,
                    lockoutTier: prompt.LockoutTier,
                    showRememberCheckbox: canRemember,
                    contactName: _currentUnattendedContact?.Name)
                {
                    Owner = mainWindow,
                };
                var result = dlg.ShowDialog() == true;
                if (result)
                {
                    _pendingPasswordToRemember = dlg.Password;
                    _rememberPasswordRequested = dlg.RememberPassword;
                }
                tcs.TrySetResult(result ? dlg.Password : null);
            }
            catch (Exception ex)
            {
                _logService.Warn("UnattendedAuth", $"password_dialog_failed: {ex.Message}");
                tcs.TrySetResult(null); // fallback — skip path
            }
            finally
            {
                // Workaround: после ShowDialog Activated иногда не fire'ится на MainWindow
                // (focus уходит не туда) → scrim остаётся dim. Идентично паттерну на
                // MainWindow.xaml.cs ~line 585 для других модалок.
                mainWindow?.ForceScrimRefresh();
            }
        }, null);

        return tcs.Task;
    }
}
