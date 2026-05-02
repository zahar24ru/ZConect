using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Media;

namespace UiApp.Converters;

/// <summary>Returns the first non-whitespace character of the contact name (upper-case),
/// or '?' if empty. Used for the avatar letter.</summary>
public sealed class NameToInitialConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s)) return "?";
        var ch = s.Trim().FirstOrDefault(c => !char.IsWhiteSpace(c));
        return ch == default ? "?" : char.ToUpper(ch).ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Deterministic color from a string hash — contact name ⇒ stable avatar color.
/// Uses pleasant, saturated hues (avoids dull grays).</summary>
public sealed class NameToColorConverter : IValueConverter
{
    // Curated palette — visually distinct, work on both light/dark backgrounds.
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4F, 0x8B, 0xF5), // blue
        Color.FromRgb(0x7B, 0x61, 0xFF), // purple
        Color.FromRgb(0x23, 0xA6, 0x6A), // green
        Color.FromRgb(0xE8, 0x7A, 0x1E), // orange
        Color.FromRgb(0xD6, 0x44, 0x6E), // pink
        Color.FromRgb(0x1E, 0xA9, 0xAD), // teal
        Color.FromRgb(0xC2, 0x9B, 0x24), // gold
        Color.FromRgb(0x5E, 0x72, 0x8A), // slate
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string ?? string.Empty;
        // Simple deterministic hash (FNV-style) — stable across runs.
        uint hash = 2166136261u;
        foreach (var c in s)
        {
            hash ^= c;
            hash *= 16777619;
        }
        var color = Palette[(int)(hash % (uint)Palette.Length)];
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
