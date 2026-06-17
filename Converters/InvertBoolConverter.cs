using System.Globalization;

namespace Mucka.Converters;

public sealed class InvertBoolConverter : Microsoft.Maui.Controls.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}
