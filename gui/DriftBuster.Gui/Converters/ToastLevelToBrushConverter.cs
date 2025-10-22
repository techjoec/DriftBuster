using System;
using System.Globalization;

using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

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

            if (TryGetResource(resourceKey, out var resource) && resource is IBrush brush)
            {
                return brush;
            }

            return Brushes.Gray;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private static bool TryGetResource(object key, out object? resource)
        {
            var app = Application.Current;
            if (app is null)
            {
                resource = null;
                return false;
            }

            var theme = app.ActualThemeVariant ?? ThemeVariant.Default;
            return app.TryGetResource(key, theme, out resource);
        }
    }
}
