using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
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
                if (OwnFileCheck.Run() is { } failure)
                {
                    ShowStartFailure(desktop, failure);
                }
                else
                {
                    StartMainWindow(desktop);
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        // A file of ours that cannot be read stops the app before anything could overwrite it.
        private static void ShowStartFailure(IClassicDesktopStyleApplicationLifetime desktop, string failure)
        {
            var window = new TextViewerWindow(
                "DriftBuster cannot start",
                "A DriftBuster file could not be read",
                failure,
                "Nothing was changed. Fix the file or move it aside, then start DriftBuster again.",
                wrap: true)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            window.Closed += (_, _) => desktop.Shutdown(1);
            desktop.MainWindow = window;
        }

        private void StartMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = new MainWindowViewModel();

            // The app opens on the comparison of servers, the task most people start it for.
            mainViewModel.ShowMultiServer();
            desktop.MainWindow = new MainWindow { DataContext = mainViewModel };

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
    }
}
