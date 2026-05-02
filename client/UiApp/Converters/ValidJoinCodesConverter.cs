using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace UiApp.Converters;

/// <summary>
/// MultiBinding converter: returns true when ALL input strings are exactly
/// 8 digits. Used to enable the Join session button only when login and pass
/// codes are both complete and valid.
/// </summary>
public sealed class ValidJoinCodesConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        foreach (var v in values)
        {
            if (v is not string s || s.Length != 8 || !s.All(char.IsDigit))
                return false;
        }
        return true;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
