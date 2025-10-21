using System;
using Avalonia.Data.Converters;

namespace DriftBuster.Gui.Converters
{
    public sealed class BooleanNegationConverter : IValueConverter
    {
        public static BooleanNegationConverter Instance { get; } = new();

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is bool flag)
            {
                return !flag;
            }

            if (value is bool?)
            {
                var nullable = (bool?)value;
                return !(nullable ?? false);
            }

            return true;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is bool flag)
            {
                return !flag;
            }

            if (value is bool?)
            {
                var nullable = (bool?)value;
                return !(nullable ?? false);
            }

            return true;
        }
    }
}
