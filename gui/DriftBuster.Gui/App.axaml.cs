using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using DriftBuster.Gui.Services;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;
using DriftBuster.Gui.Headless;

namespace DriftBuster.Gui
{
    [ExcludeFromCodeCoverage]
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            EnsureFontResources(this);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(),
                };

            }

            base.OnFrameworkInitializationCompleted();
        }

        internal static void EnsureFontResources(Application app)
        {
            const string key = "fonts:SystemFonts";

            if (app.Resources.TryGetValue(key, out var value) && value is ConcurrentDictionary<string, FontFamily> existing)
            {
                existing.TryAdd("Inter", new FontFamily("Inter"));
                existing.TryAdd(key, new FontFamily("Inter"));
                return;
            }

            var dictionary = new ConcurrentDictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase)
            {
                ["Inter"] = new FontFamily("Inter"),
                [key] = new FontFamily("Inter"),
            };

            app.Resources[key] = dictionary;

            var fontManager = FontManager.Current;
            HeadlessFontBootstrapper.EnsureSystemFonts(fontManager);
        }
    }
}
