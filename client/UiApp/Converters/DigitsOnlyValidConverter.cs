using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace UiApp.Converters;

/// <summary>
/// Returns true if the string is empty OR contains only digits (0-9).
/// Returns false if it contains any non-digit character.
/// Used by DigitsOnlyTextBoxStyle to show a red border on invalid input.
/// </summary>
public sealed class DigitsOnlyValidConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrEmpty(s)) return true;
        return s.All(char.IsDigit);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
