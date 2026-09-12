using System;
using System.Diagnostics.CodeAnalysis;

using Avalonia;
using Avalonia.Media;

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
                .LogToTrace();
    }
}
