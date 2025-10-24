using System;
using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Styling;

using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Converters
{
    public sealed class ToastLevelToIconConverter : IValueConverter
    {
        public static ToastLevelToIconConverter Instance { get; } = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not ToastLevel level)
            {
                return string.Empty;
            }

            var resourceKey = level switch
            {
                ToastLevel.Success => "Toast.Icon.Success",
                ToastLevel.Warning => "Toast.Icon.Warning",
                ToastLevel.Error => "Toast.Icon.Error",
                _ => "Toast.Icon.Info",
            };

            if (TryGetResource(resourceKey, out var resource))
            {
                return resource;
            }

            return string.Empty;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

        private static bool TryGetResource(object key, out object? resource)
        {
            var app = Application.Current;
            if (app is null)
            {
                resource = null;
                return false;
            }

            if (app.TryFindResource(key, out resource))
            {
                return true;
            }

            var theme = app.ActualThemeVariant ?? ThemeVariant.Default;
            return app.TryGetResource(key, theme, out resource);
        }
    }
}
