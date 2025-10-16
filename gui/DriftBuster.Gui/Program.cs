using System;
using System.Diagnostics.CodeAnalysis;

using Avalonia;

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
                .WithInterFont()
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
