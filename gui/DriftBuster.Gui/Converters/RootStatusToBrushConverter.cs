using System;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Converters
{
    public sealed class RootStatusToBrushConverter : IValueConverter
    {
        public static RootStatusToBrushConverter Instance { get; } = new();

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            var state = value is RootValidationState typed
                ? typed
                : RootValidationState.Pending;
            return state switch
            {
                RootValidationState.Valid => SolidColorBrush.Parse("#144f2a"),
                RootValidationState.Invalid => SolidColorBrush.Parse("#64241f"),
                _ => SolidColorBrush.Parse("#1e293b"),
            };
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
