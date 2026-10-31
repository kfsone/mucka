using System.Globalization;

namespace Mucka.Converters;

/// <summary>true shows (1), false hides (0) by opacity: the element keeps its place in the layout.
/// For status icons in fixed slots, which are never collapsed - see EffectSlotsView.xaml.</summary>
public sealed class ShownOpacityConverter : Microsoft.Maui.Controls.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d && d > 0;
}
