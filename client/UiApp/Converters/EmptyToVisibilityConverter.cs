using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UiApp.Converters;

/// <summary>Пустая или null строка → Visible, иначе → Collapsed (для плейсхолдеров).</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        return string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
