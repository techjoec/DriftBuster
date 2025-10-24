using System;
using System.Collections.Generic;
using System.Globalization;

using Avalonia.Data;
using Avalonia.Data.Converters;

namespace DriftBuster.Gui.Converters
{
    public sealed class DiffJsonViewModeMatchConverter : IMultiValueConverter
    {
        public static DiffJsonViewModeMatchConverter Instance { get; } = new();

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count < 2)
            {
                return false;
            }

            var left = values[0];
            var right = values[1];

            if (left is null || right is null)
            {
                return false;
            }

            return Equals(left, right);
        }

        public object? ConvertBack(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            return BindingOperations.DoNothing;
        }
    }
}
