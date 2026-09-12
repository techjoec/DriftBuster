using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };

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
    }
}
