using System.ComponentModel;
using System.Runtime.CompilerServices;
using UiApp.Properties;

namespace UiApp.Models;

/// <summary>
/// Единый view-model для Recent row. Combines two sources:
///  - <see cref="Contact"/> с недавним LastConnectedUtc (named, saved) → IsSaved=true
///  - <see cref="RecentConnection"/> записи (ad-hoc history) → IsSaved=false
///
/// В DataTemplate привязывается к свойствам через IsSaved DataTrigger:
///  - Avatar background: PrimaryBrush (saved) / PlaceholderBrush (ephemeral)
///  - FirstLetter: первая буква имени (saved) / «?» (ephemeral)
///  - Кнопка «Сохранить в контакты» (📇) visible только когда IsSaved=false
///  - Presence dot (🟢/🔴/⚪) — только для saved; ephemeral всегда Unknown (нет
///    login code в address book чтобы спросить /api/v1/presence).
///
/// Поддерживает INotifyPropertyChanged — при изменении SourceContact.Presence
/// forward'аем событие на PresenceBrush/PresenceTooltip, чтобы binding в Recent
/// card обновился live (PresenceService меняет Contact.Presence каждые 30 сек).
///
/// Не хранится — создаётся каждый раз в RecentContacts getter из Contacts + History.
/// </summary>
public sealed class RecentItem : INotifyPropertyChanged
{
    /// <summary>True если источник — сохранённый Contact. False если это ad-hoc
    /// запись из RecentConnection history (ephemeral).</summary>
    public bool IsSaved { get; init; }

    /// <summary>Имя для UI: либо Contact.Name (saved), либо «Сеанс XXXX» (ephemeral,
    /// последние 4 цифры login code).</summary>
    public string DisplayName { get; init; } = string.Empty;

    public string LoginCode { get; init; } = string.Empty;
    public string PassCode { get; init; } = string.Empty;

    public DateTime LastConnectedUtc { get; init; }

    private Contact? _sourceContact;
    /// <summary>Валиден только для IsSaved=true — used для connect через address book
    /// flow (ConnectToSpecificContactAsync + saved unattended password).
    /// При set subscribe'имся на Contact.PropertyChanged чтобы forward'ить presence
    /// updates в наши own bindings.</summary>
    public Contact? SourceContact
    {
        get => _sourceContact;
        init
        {
            if (_sourceContact is not null)
                _sourceContact.PropertyChanged -= OnSourceContactChanged;
            _sourceContact = value;
            if (_sourceContact is not null)
                _sourceContact.PropertyChanged += OnSourceContactChanged;
        }
    }

    private void OnSourceContactChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Forward presence-related changes — WPF binding на Recent card
        // увидит update без полной пересборки RecentContacts коллекции.
        if (e.PropertyName == nameof(Contact.Presence) ||
            e.PropertyName == nameof(Contact.PresenceBrush) ||
            e.PropertyName == nameof(Contact.PresenceTooltip))
        {
            OnPropertyChanged(nameof(Presence));
            OnPropertyChanged(nameof(PresenceBrush));
            OnPropertyChanged(nameof(PresenceTooltip));
        }
    }

    /// <summary>Аватарный символ. «?» для ephemeral; первая буква для saved.</summary>
    public string FirstLetter
    {
        get
        {
            if (!IsSaved) return "?";
            if (string.IsNullOrWhiteSpace(DisplayName)) return "?";
            return DisplayName.Trim().Substring(0, 1).ToUpperInvariant();
        }
    }

    /// <summary>Human-friendly relative time — локализовано (ru/en), формат из Strings.*</summary>
    public string LastConnectedDisplay
    {
        get
        {
            var delta = DateTime.UtcNow - LastConnectedUtc;
            if (delta.TotalSeconds < 60) return Strings.Time_JustNow;
            if (delta.TotalMinutes < 60) return string.Format(Strings.Time_MinutesAgo_Format, (int)delta.TotalMinutes);
            if (delta.TotalHours < 24)   return string.Format(Strings.Time_HoursAgo_Format, (int)delta.TotalHours);
            if (delta.TotalDays < 2)     return Strings.Time_Yesterday;
            if (delta.TotalDays < 7)     return string.Format(Strings.Time_DaysAgo_Format, (int)delta.TotalDays);
            if (delta.TotalDays < 30)    return string.Format(Strings.Time_WeeksAgo_Format, (int)(delta.TotalDays / 7));
            return string.Format(Strings.Time_MonthsAgo_Format, (int)(delta.TotalDays / 30));
        }
    }

    /// <summary>Есть ли saved unattended password (🔒 icon в карточке). Только для saved.</summary>
    public bool HasSavedPassword =>
        IsSaved && SourceContact is not null && !string.IsNullOrEmpty(SourceContact.SavedUnattendedPassword);

    /// <summary>Live presence state для Recent card dot. Делегируется к SourceContact
    /// для saved (PresenceService обновляет через LoginCode lookup). Для ephemeral
    /// (IsSaved=false) — всегда Unknown: эти login codes не sync'атся с address book,
    /// PresenceService не имеет основания их polling'овать (privacy + noise on server).</summary>
    public Services.PresenceState Presence =>
        IsSaved && SourceContact is not null
            ? SourceContact.Presence
            : Services.PresenceState.Unknown;

    public System.Windows.Media.Brush PresenceBrush =>
        IsSaved && SourceContact is not null
            ? SourceContact.PresenceBrush
            : System.Windows.Media.Brushes.LightGray;

    public string PresenceTooltip =>
        IsSaved && SourceContact is not null
            ? SourceContact.PresenceTooltip
            : Strings.Presence_Unknown_Tooltip;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
