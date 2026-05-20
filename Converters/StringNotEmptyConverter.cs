using System.Globalization;

namespace Mucka.Converters;

/// <summary>Returns true if the string is non-null and non-empty; used to show/hide dreamword chip.</summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s.Length > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
