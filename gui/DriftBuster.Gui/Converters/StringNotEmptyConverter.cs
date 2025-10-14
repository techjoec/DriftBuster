using System;
using System.Globalization;

using Avalonia.Data.Converters;

namespace DriftBuster.Gui.Converters;

public sealed class StringNotEmptyConverter : IValueConverter
{
    public static readonly StringNotEmptyConverter Instance = new();

    private StringNotEmptyConverter() { }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text)
        {
            return !string.IsNullOrWhiteSpace(text);
        }

        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
