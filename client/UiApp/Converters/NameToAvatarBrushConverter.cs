using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace UiApp.Converters;

/// <summary>
/// Детерминированно выбирает один из 8 gradient brush'ей по hash'у имени. Используется
/// для avatar в Recent card — разные контакты получают разные цвета, визуально
/// живо и легко отличать.
///
/// Одно и то же имя всегда даёт один и тот же градиент (hash-based), поэтому user
/// привыкает: «Мамин ПК» всегда малиновый, «Офис» — изумрудный, и т.п. Это хороший
/// UX — цвет становится вторичным identifier'ом рядом с первой буквой.
/// </summary>
public sealed class NameToAvatarBrushConverter : IValueConverter
{
    // 8 современных gradient-пар (Tailwind-style 500→700 ranges).
    // Разнообразие hue, достаточный контраст с белым текстом (WCAG AA for 14pt).
    private static readonly (string from, string to)[] Palette = new[]
    {
        ("#6366F1", "#4F46E5"),  // indigo
        ("#EC4899", "#DB2777"),  // pink
        ("#F59E0B", "#D97706"),  // amber
        ("#10B981", "#059669"),  // emerald
        ("#8B5CF6", "#7C3AED"),  // violet
        ("#EF4444", "#DC2626"),  // red
        ("#06B6D4", "#0891B2"),  // cyan
        ("#F97316", "#EA580C"),  // orange
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var name = value as string;
        if (string.IsNullOrEmpty(name))
            return new SolidColorBrush(Colors.Gray);

        // Sum of char codes — simple deterministic hash. Одинаковое имя всегда
        // попадает в один и тот же bucket, пользователь привыкает к цвету контакта.
        int hash = 0;
        foreach (var ch in name) hash = unchecked(hash * 31 + ch);
        var idx = Math.Abs(hash) % Palette.Length;
        var pair = Palette[idx];

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(pair.from)!, 0));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(pair.to)!, 1));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
