using Avalonia.Markup.Xaml;

namespace DriftBuster.Gui.Views
{
    public partial class MainWindow : Avalonia.Controls.Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
