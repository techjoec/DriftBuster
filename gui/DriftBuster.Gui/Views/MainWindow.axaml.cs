using System;

using Avalonia.Markup.Xaml;

using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Views
{
    public partial class MainWindow : Avalonia.Controls.Window
    {
        private readonly IDisposable _responsiveSubscription;

        public MainWindow()
        {
            InitializeComponent();
            _responsiveSubscription = ResponsiveLayoutService.Attach(this, ResponsiveSpacingProfiles.MainWindow);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _responsiveSubscription.Dispose();
        }
    }
}
