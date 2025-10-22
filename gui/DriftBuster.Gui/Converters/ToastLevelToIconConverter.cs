using System;
using System.Globalization;

using Avalonia;
using Avalonia.Data.Converters;

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

            return Application.Current?.TryFindResource(resourceKey, out var resource) == true
                ? resource
                : string.Empty;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
