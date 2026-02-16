using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using DriftBuster.Gui.Headless;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;

namespace DriftBuster.Gui
{
    [ExcludeFromCodeCoverage]
    public partial class App : Application
    {
#if DEBUG
        private AutomationServer? _automationServer;
#endif

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            EnsureFontResources(this);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = CreateMainWindowWithFontFallback();

#if DEBUG
                if (string.Equals(Environment.GetEnvironmentVariable("DRIFTBUSTER_AUTOMATION"), "1", StringComparison.Ordinal))
                {
                    var mainVm = (MainWindowViewModel)desktop.MainWindow.DataContext!;
                    _automationServer = new AutomationServer(new AutomationDispatcher(mainVm));
                    _automationServer.Start();
                    desktop.ShutdownRequested += (_, _) => _automationServer?.Dispose();
                }
#endif
            }

            base.OnFrameworkInitializationCompleted();
        }

        // Derived from publicly documented behavior, not vendor source.
        // On some .NET 10.0.x versions the embedded Inter font collection is not
        // resolved before the Compositor creates its DiagnosticTextRenderer,
        // causing "Could not create glyphTypeface. Font family: $Default (key: )".
        // Root cause: FontFamily("Inter") has no URI key, so FontManager only
        // searches SystemFonts (which lacks embedded Inter). Retarget the default
        // to "fonts:Inter#Inter" which explicitly hits the InterFontCollection.
        private static MainWindow CreateMainWindowWithFontFallback()
        {
            try
            {
                return new MainWindow { DataContext = new MainWindowViewModel() };
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("glyphTypeface", StringComparison.Ordinal))
            {
                RepairDefaultFontFamily();

                return new MainWindow { DataContext = new MainWindowViewModel() };
            }
        }

        private static void RepairDefaultFontFamily()
        {
            var fontManager = FontManager.Current;
            var backingField = typeof(FontManager).GetField(
                "<DefaultFontFamily>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (backingField is not null)
            {
                backingField.SetValue(fontManager, new FontFamily("fonts:Inter#Inter"));
            }

            HeadlessFontBootstrapper.RepairDesktopFontResolution();
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
            HeadlessFontBootstrapper.EnsureSystemFontsDictionary(fontManager);
        }
    }
}
