using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DriftBuster.Gui.Converters
{
    public sealed class BoolToBrushConverter : IValueConverter
    {
        public static readonly BoolToBrushConverter Instance = new();

        private BoolToBrushConverter() { }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isTrue = value is true;
            if (parameter is string param && param.Contains(";"))
            {
                var parts = param.Split(';', 2);
                var trueColor = Color.Parse(parts[0].Trim());
                var falseColor = Color.Parse(parts[1].Trim());
                return new SolidColorBrush(isTrue ? trueColor : falseColor);
            }

            // Fallback to accent vs border
            var accent = Color.Parse("#FF3B82F6");
            var border = Color.Parse("#FF3B4A5A");
            return new SolidColorBrush(isTrue ? accent : border);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
