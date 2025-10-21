using System;
using System.Globalization;

using Avalonia.Data.Converters;
using Avalonia.Media;

using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Converters
{
    public sealed class ToastLevelToBrushConverter : IValueConverter
    {
        public static ToastLevelToBrushConverter Instance { get; } = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not ToastLevel level)
            {
                return Brushes.Gray;
            }

            return level switch
            {
                ToastLevel.Success => new SolidColorBrush(Color.Parse("#14532D")),
                ToastLevel.Warning => new SolidColorBrush(Color.Parse("#9A3412")),
                ToastLevel.Error => new SolidColorBrush(Color.Parse("#7F1D1D")),
                _ => new SolidColorBrush(Color.Parse("#1D4ED8")),
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
