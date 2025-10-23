using System;
using System.Diagnostics.CodeAnalysis;

using Avalonia;
using Avalonia.Media;

using DriftBuster.Gui.Headless;

using Velopack;

namespace DriftBuster.Gui
{
    [ExcludeFromCodeCoverage]
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            VelopackApp.Build().Run();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(new FontManagerOptions
                {
                    DefaultFamilyName = "Inter",
                    FontFallbacks = new[]
                    {
                        new FontFallback
                        {
                            FontFamily = new FontFamily("Inter")
                        }
                    }
                })
                .WithInterFont()
                .AfterSetup(_ =>
                {
                    if (Application.Current is App app)
                    {
                        App.EnsureFontResources(app);
                    }
                })
                .LogToTrace();

        private static readonly object HeadlessSync = new();
        private static bool _headlessInitialized;

        internal static IDisposable EnsureHeadless(Func<AppBuilder, AppBuilder>? configure = null)
        {
            lock (HeadlessSync)
            {
                if (_headlessInitialized && Application.Current is App)
                {
                    return HeadlessScope.Instance;
                }

                var builder = BuildAvaloniaApp();
                builder = configure?.Invoke(builder) ?? builder;

                builder.SetupWithoutStarting();
                HeadlessFontBootstrapper.Ensure(builder);
                if (Application.Current is App app)
                {
                    App.EnsureFontResources(app);
                }
                _headlessInitialized = true;

                return HeadlessScope.Instance;
            }
        }

        private sealed class HeadlessScope : IDisposable
        {
            public static readonly HeadlessScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
