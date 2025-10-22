using System;
using System.Globalization;

using Avalonia;
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

            var resourceKey = level switch
            {
                ToastLevel.Success => "Brush.Toast.Success",
                ToastLevel.Warning => "Brush.Toast.Warning",
                ToastLevel.Error => "Brush.Toast.Error",
                _ => "Brush.Toast.Info",
            };

            if (Application.Current?.TryFindResource(resourceKey, out var resource) == true && resource is IBrush brush)
            {
                return brush;
            }

            return Brushes.Gray;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
