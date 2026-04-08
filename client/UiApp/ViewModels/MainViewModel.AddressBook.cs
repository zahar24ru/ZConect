using System.Linq;
using UiApp.Dialogs;
using UiApp.Models;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel
{
    private void AddContact()
    {
        var name = (NewContactName ?? string.Empty).Trim();
        var server = (NewContactServer ?? string.Empty).Trim();
        var login = (NewContactLogin ?? string.Empty).Trim();
        var pass = (NewContactPass ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
        {
            StatusText = "Введите имя контакта";
            OnPropertyChanged(nameof(StatusText));
            return;
        }
        if (string.IsNullOrEmpty(server))
        {
            StatusText = "Введите адрес сервера";
            OnPropertyChanged(nameof(StatusText));
            return;
        }
        if (login.Length != 8 || pass.Length != 8)
        {
            StatusText = "Логин и пароль — по 8 цифр";
            OnPropertyChanged(nameof(StatusText));
            return;
        }
        if (EditingContact is not null)
        {
            EditingContact.Name = name;
            EditingContact.ServerApiBaseUrl = server;
            EditingContact.LoginCode = login;
            EditingContact.PassCode = pass;
            SaveContacts();
            var prev = EditingContact;
            EditingContact = null;
            NewContactName = NewContactServer = NewContactLogin = NewContactPass = string.Empty;
            StatusText = "Контакт изменён";
            OnPropertyChanged(nameof(StatusText));
            _logService.Info("UiApp", "address_book_contact_edited name=" + prev.Name);
            return;
        }
        var contact = new Contact { Name = name, ServerApiBaseUrl = server, LoginCode = login, PassCode = pass };
        Contacts.Add(contact);
        SaveContacts();
        NewContactName = NewContactServer = NewContactLogin = NewContactPass = string.Empty;
        StatusText = "Контакт добавлен";
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
        StatusText = "Редактирование контакта";
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
        if (!AppDialog.Confirm($"Удалить контакт «{contact.Name}»?")) return;

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
        StatusText = "Контакт удалён";
        OnPropertyChanged(nameof(StatusText));
        _logService.Info("UiApp", "address_book_contact_removed name=" + name);
    }

    private void SaveContacts()
    {
        _addressBookService.Save(Contacts.ToList());
    }

    private async Task ConnectToContactAsync()
    {
        if (SelectedContact is null)
        {
            StatusText = "Выберите контакт";
            OnPropertyChanged(nameof(StatusText));
            return;
        }
        var contact = SelectedContact;
        _logService.Info("UiApp", "connect_to_contact name=" + contact.Name);
        await ConnectAsViewerAsync(contact.ServerApiBaseUrl, contact.LoginCode, contact.PassCode, contact.Name);
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
            StatusText = "Окно удалённого экрана открыто";
            OnPropertyChanged(nameof(StatusText));
        }
    }
}
