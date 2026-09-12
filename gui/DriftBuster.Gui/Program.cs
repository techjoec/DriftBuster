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
        internal const string InterFontFamily = "fonts:Inter#Inter";

        [STAThread]
        public static void Main(string[] args)
        {
            VelopackApp.Build().Run();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // The family must carry the collection URI: a bare "Inter" only searches the
                // system fonts, so the embedded collection registered by WithInterFont is never
                // consulted on a machine without Inter installed.
                .With(new FontManagerOptions
                {
                    DefaultFamilyName = InterFontFamily,
                    FontFallbacks = new[]
                    {
                        new FontFallback
                        {
                            FontFamily = new FontFamily(InterFontFamily)
                        }
                    }
                })
                .WithInterFont()
                .LogToTrace();
    }
}
