using System.Linq;
using UiApp.Dialogs;
using UiApp.Models;
using UiApp.Properties;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>M2: предельная длина имени контакта. Защита от огромных inputs
    /// (user paste'нул мегабайты в Name). XAML MaxLength=64 уже ограничивает UI,
    /// но serve в VM как defense-in-depth на случай если каким-то путём обойдёт.</summary>
    private const int MaxNameLength = 64;

    private void AddContact()
    {
        var name = (NewContactName ?? string.Empty).Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        var login = (NewContactLogin ?? string.Empty).Trim();
        var pass = (NewContactPass ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
        {
            StatusText = Strings.Status_ContactNameEmpty;
            OnPropertyChanged(nameof(StatusText));
            return;
        }
        if (login.Length != 8 || pass.Length != 8 || !login.All(char.IsDigit) || !pass.All(char.IsDigit))
        {
            StatusText = Strings.Status_InvalidContactCode;
            OnPropertyChanged(nameof(StatusText));
            return;
        }
        // Server address is inherited from Settings → Server → Server API URL (empty = default).
        if (EditingContact is not null)
        {
            EditingContact.Name = name;
            EditingContact.LoginCode = login;
            EditingContact.PassCode = pass;
            SaveContacts();
            var prev = EditingContact;
            EditingContact = null;
            NewContactName = NewContactServer = NewContactLogin = NewContactPass = string.Empty;
            StatusText = Strings.Status_ContactModified;
            OnPropertyChanged(nameof(StatusText));
            _logService.Info("UiApp", "address_book_contact_edited name=" + prev.Name);
            return;
        }
        var contact = new Contact { Name = name, ServerApiBaseUrl = string.Empty, LoginCode = login, PassCode = pass };
        Contacts.Add(contact);
        SaveContacts();
        RefreshRecentContacts();
        NewContactName = NewContactServer = NewContactLogin = NewContactPass = string.Empty;
        // Auto-select новый контакт — ListBox scroll'ит его into view, user видит результат.
        SelectedContact = contact;
        StatusText = $"Контакт «{name}» добавлен";
        OnPropertyChanged(nameof(StatusText));
        _logService.Info("UiApp", "address_book_contact_added name=" + name);
    }

    private void EditContact(Contact contact)
    {
        SelectedContact = contact;
        EditingContact = contact;
        NewContactName = contact.Name;
        NewContactServer = contact.ServerApiBaseUrl;
        NewContactLogin = contact.LoginCode;
        NewContactPass = contact.PassCode;
        StatusText = Strings.Status_EditingContact;
        OnPropertyChanged(nameof(StatusText));
    }

    private void CancelEditContact()
    {
        EditingContact = null;
        NewContactName = NewContactServer = NewContactLogin = NewContactPass = string.Empty;
        StatusText = string.Empty;
        OnPropertyChanged(nameof(StatusText));
    }

    private void RemoveContact(Contact contact)
    {
        if (!AppDialog.Confirm(string.Format(Strings.AddressBook_Remove_Confirm_Format, contact.Name))) return;

        var name = contact.Name;
        if (EditingContact == contact)
        {
            EditingContact = null;
            NewContactName = NewContactServer = NewContactLogin = NewContactPass = string.Empty;
        }
        if (SelectedContact == contact)
            SelectedContact = null;
        Contacts.Remove(contact);
        SaveContacts();
        RefreshRecentContacts();
        StatusText = Strings.Status_ContactDeleted;
        OnPropertyChanged(nameof(StatusText));
        _logService.Info("UiApp", "address_book_contact_removed name=" + name);
    }

    /// <summary>Call after any modification to Contacts collection to push RecentContacts
    /// (computed property) through WPF binding update. ObservableCollection notifies about
    /// item changes but RecentContacts is a separate query — binding не видит изменений
    /// без explicit invalidation.</summary>
    private void RefreshRecentContacts()
    {
        OnPropertyChanged(nameof(RecentContacts));
        OnPropertyChanged(nameof(HasRecentContacts));
    }

    private void SaveContacts()
    {
        _addressBookService.Save(Contacts.ToList());
    }

    private async Task ConnectToContactAsync()
    {
        if (SelectedContact is null)
        {
            StatusText = Strings.Status_SelectContact;
            OnPropertyChanged(nameof(StatusText));
            return;
        }
        await ConnectToSpecificContactAsync(SelectedContact);
    }

    /// <summary>Подключение к явно переданному контакту (без SelectedContact). Используется
    /// из recent-карточек на вкладке «Создать сессию» и из address book через dispatch'ер
    /// ConnectToContactAsync(). После successful connect обновляет Contact.LastConnectedUtc
    /// и сохраняет адресную книгу — Recent row refresh'ится автоматически через PropertyChanged.</summary>
    internal async Task ConnectToSpecificContactAsync(Contact? contact)
    {
        if (contact is null) return;
        _logService.Info("UiApp", "connect_to_contact name=" + contact.Name);
        var serverUrl = string.IsNullOrWhiteSpace(contact.ServerApiBaseUrl) ? ServerApiBaseUrl : contact.ServerApiBaseUrl;
        // Передаём контакт в UnattendedAuth flow — auto-fill saved password + сохранение на remember.
        SetCurrentUnattendedContact(contact);
        try
        {
            await ConnectAsViewerAsync(serverUrl, contact.LoginCode, contact.PassCode, contact.Name);
            // Success — bump LastConnectedUtc и persist. Это пушит контакт в верх Recent row.
            contact.LastConnectedUtc = DateTime.UtcNow;
            try { _addressBookService.Save(Contacts.ToList()); }
            catch (Exception ex) { _logService.Warn("UiApp", $"save_addressbook_after_connect_failed: {ex.Message}"); }
            RefreshRecentContacts();
        }
        finally
        {
            SetCurrentUnattendedContact(null);
        }
    }

    /// <summary>
    /// Combined Recent row: union из (1) Contacts с недавним LastConnectedUtc — saved,
    /// named — и (2) RecentConnection history — ephemeral, ad-hoc. Dedup по LoginCode:
    /// saved Contact превалирует. Top-5 по убыванию времени. Empty → вся секция hide'ится
    /// через HasRecentContacts binding.
    /// </summary>
    public IEnumerable<RecentItem> RecentContacts
    {
        get
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var items = new List<RecentItem>();
            var savedLogins = new HashSet<string>(StringComparer.Ordinal);

            // 1. Saved contacts (named, with LastConnectedUtc set).
            foreach (var c in Contacts)
            {
                if (!c.LastConnectedUtc.HasValue || c.LastConnectedUtc.Value <= cutoff) continue;
                savedLogins.Add(c.LoginCode);
                items.Add(new RecentItem
                {
                    IsSaved = true,
                    DisplayName = c.Name,
                    LoginCode = c.LoginCode,
                    PassCode = c.PassCode,
                    LastConnectedUtc = c.LastConnectedUtc.Value,
                    SourceContact = c,
                });
            }

            // 2. History items — только те, которых нет в saved (dedup по LoginCode).
            try
            {
                foreach (var h in _recentHistoryService.Load())
                {
                    if (savedLogins.Contains(h.LoginCode)) continue;
                    if (h.LastConnectedUtc <= cutoff) continue;
                    var last4 = h.LoginCode.Length >= 4 ? h.LoginCode[^4..] : h.LoginCode;
                    items.Add(new RecentItem
                    {
                        IsSaved = false,
                        DisplayName = $"Сеанс {last4}",
                        LoginCode = h.LoginCode,
                        PassCode = h.PassCode,
                        LastConnectedUtc = h.LastConnectedUtc,
                    });
                }
            }
            catch (Exception ex)
            {
                _logService?.Warn("UiApp", $"recent_history_load_failed: {ex.Message}");
            }

            return items
                .OrderByDescending(r => r.LastConnectedUtc)
                .Take(5)
                .ToList();
        }
    }

    /// <summary>True если есть хоть один recent — для DataTrigger collapse секции.</summary>
    public bool HasRecentContacts => RecentContacts.Any();

    /// <summary>Подключение из Recent row — unified для saved и history items.
    /// Saved → ConnectToSpecificContactAsync (полный flow c unattended auth + remember).
    /// History → ad-hoc connect с login/pass из записи (без сохранения в AB).</summary>
    internal async Task ConnectRecentItemAsync(RecentItem? item)
    {
        if (item is null) return;
        if (item.IsSaved && item.SourceContact is not null)
        {
            await ConnectToSpecificContactAsync(item.SourceContact);
        }
        else
        {
            // Ad-hoc — LoginCode/PassCode из history. Success bump'нет history
            // запись через RegisterRecentAdhocConnection в ConnectAsViewerAsync.
            await ConnectAsViewerAsync(
                serverBaseUrl: ServerApiBaseUrl,
                loginCode: item.LoginCode,
                passCode: item.PassCode,
                displayName: item.DisplayName);
        }
    }

    /// <summary>Записать в историю успешный ad-hoc connect. НЕ создаёт Contact —
    /// это отдельная история (recent-history.json). Пользователь может явно сохранить
    /// запись как Contact через кнопку в Recent card.</summary>
    internal void RegisterRecentAdhocConnection(string loginCode, string passCode)
    {
        try
        {
            _recentHistoryService.AddOrBump(loginCode, passCode);
            _uiContext.Post(_ => RefreshRecentContacts(), null);
            _logService.Info("UiApp", $"recent_history_bumped login={loginCode[^4..]}");
        }
        catch (Exception ex)
        {
            _logService.Warn("UiApp", $"recent_history_add_failed: {ex.Message}");
        }
    }

    /// <summary>Command «Сохранить в контакты» из Recent card. Открывает PromptDialog
    /// для ввода имени, создаёт Contact из данных RecentItem, удаляет запись из history.
    /// M3: дедуп по LoginCode — если такой уже есть в Contacts (например, user
    /// вручную добавил), не создаём дубликат. Вместо — bump LastConnectedUtc +
    /// убираем history запись.</summary>
    internal void SaveRecentAsContact(RecentItem? item)
    {
        if (item is null || item.IsSaved) return;

        // M3 dedup: если LoginCode уже есть в AB — не создаём второй Contact.
        var existing = Contacts.FirstOrDefault(c =>
            string.Equals(c.LoginCode, item.LoginCode, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.LastConnectedUtc = DateTime.UtcNow;
            try { _addressBookService.Save(Contacts.ToList()); }
            catch (Exception ex) { _logService.Warn("UiApp", $"save_after_dedup_failed: {ex.Message}"); }
            try { _recentHistoryService.Remove(item.LoginCode); }
            catch (Exception ex) { _logService.Warn("UiApp", $"remove_from_history_failed: {ex.Message}"); }
            RefreshRecentContacts();
            StatusText = $"«{existing.Name}» уже был в адресной книге — обновили время";
            OnPropertyChanged(nameof(StatusText));
            _logService.Info("UiApp", $"recent_dedup_existing_contact name=\"{existing.Name}\"");
            return;
        }

        var name = AppDialog.Prompt(
            title: Strings.SaveToAddressBook_Title,
            message: Strings.SaveToAddressBook_Message,
            initialValue: string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return;

        // M2 defense-in-depth: обрезать имя до MaxNameLength, PromptDialog.MaxLength=64
        // уже ограничивает UI но на случай если обошёл.
        var trimmedName = name.Trim();
        if (trimmedName.Length > MaxNameLength) trimmedName = trimmedName[..MaxNameLength];

        var contact = new Contact
        {
            Name = trimmedName,
            ServerApiBaseUrl = string.Empty,
            LoginCode = item.LoginCode,
            PassCode = item.PassCode,
            LastConnectedUtc = item.LastConnectedUtc,
            IsAutoGenerated = false,
        };
        Contacts.Add(contact);
        try { _addressBookService.Save(Contacts.ToList()); }
        catch (Exception ex) { _logService.Warn("UiApp", $"save_contact_from_history_failed: {ex.Message}"); }

        // Убираем из history — теперь контакт «живёт» в Address Book.
        try { _recentHistoryService.Remove(item.LoginCode); }
        catch (Exception ex) { _logService.Warn("UiApp", $"remove_from_history_failed: {ex.Message}"); }

        RefreshRecentContacts();
        StatusText = string.Format(Strings.Status_ContactSavedFromHistory_Format, contact.Name);
        OnPropertyChanged(nameof(StatusText));
        _logService.Info("UiApp", $"recent_saved_as_contact name=\"{contact.Name}\"");
    }

    /// <summary>One-time миграция: если есть Contact с IsAutoGenerated=true, переносим в
    /// history и удаляем из Contacts. Идемпотентно: при повторном запуске ephemeral
    /// не будет — no-op.</summary>
    private void MigrateLegacyEphemeralContacts()
    {
        try
        {
            var ephemeral = Contacts.Where(c => c.IsAutoGenerated).ToList();
            if (ephemeral.Count == 0) return;
            foreach (var c in ephemeral)
            {
                try { _recentHistoryService.AddOrBump(c.LoginCode, c.PassCode); } catch { }
                Contacts.Remove(c);
            }
            try { _addressBookService.Save(Contacts.ToList()); } catch { }
            _logService.Info("UiApp", $"migrated_ephemeral_contacts_to_history count={ephemeral.Count}");
        }
        catch (Exception ex)
        {
            _logService.Warn("UiApp", $"migrate_ephemeral_failed: {ex.Message}");
        }
    }

    /// <summary>Menu → «Очистить «Недавно подключённые»». НЕ удаляет Contacts — только:
    ///  - Очищает history (recent-history.json полностью).
    ///  - Сбрасывает LastConnectedUtc=null у всех Contacts (исчезают из Recent row,
    ///    но остаются в Address Book полноценными записями).
    /// Пользователь может явно удалить контакт через Remove в Address Book tab.</summary>
    public void ClearRecentContacts()
    {
        try { _recentHistoryService.Clear(); }
        catch (Exception ex) { _logService.Warn("UiApp", $"clear_history_failed: {ex.Message}"); }

        foreach (var c in Contacts)
        {
            if (c.LastConnectedUtc.HasValue) c.LastConnectedUtc = null;
        }
        try { _addressBookService.Save(Contacts.ToList()); }
        catch (Exception ex) { _logService.Warn("UiApp", $"save_after_clear_recent_failed: {ex.Message}"); }

        RefreshRecentContacts();
        StatusText = "Недавно подключённые очищены";
        OnPropertyChanged(nameof(StatusText));
        _logService.Info("UiApp", "recent_cleared history + timestamps reset");
    }

    private async Task OpenRemoteDesktopAsync()
    {
        if (_role != ConnectionRole.Viewer || _signalingClient is null)
        {
            StatusText = "Доступно только при подключении как клиент (зритель)";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        if (_fileTransferOnly && _realPeerAgent is not null)
        {
            try
            {
                StatusText = "Подключение удалённого стола...";
                OnPropertyChanged(nameof(StatusText));
                _realPeerAgent.AddVideoTransceiverIfNeeded();
                var sdp = await _realPeerAgent.CreateOfferAsync(_lifetimeCts.Token);
                await _signalingClient.SendAsync("offer", _currentSessionId, new { sdp }, _lifetimeCts.Token);
                _logService.Info("Signaling", "renegotiation_offer_sent_for_remote_desktop");
            }
            catch (Exception ex)
            {
                StatusText = "Ошибка запроса удалённого стола: " + ex.Message;
                OnPropertyChanged(nameof(StatusText));
                _logService.Error("UiApp", "open_remote_desktop_renegotiation_failed", ex.Message);
                return;
            }
        }

        RemoteScreenWindowRequested?.Invoke();
        if (!_fileTransferOnly)
        {
            StatusText = Strings.Status_RemoteScreenOpened;
            OnPropertyChanged(nameof(StatusText));
        }
    }
}
