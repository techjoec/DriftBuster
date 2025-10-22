using System;
using System.Collections;
using System.Globalization;

using Avalonia.Data.Converters;

namespace DriftBuster.Gui.Converters
{
    public sealed class CountToBooleanConverter : IValueConverter
    {
        public static CountToBooleanConverter Instance { get; } = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is int count)
            {
                return count > 0;
            }

            if (value is IEnumerable enumerable)
            {
                foreach (var _ in enumerable)
                {
                    return true;
                }

                return false;
            }

            return false;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
