using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DriftBuster.Gui.Views
{
    public partial class ConfigDrilldownView : UserControl
    {
        public ConfigDrilldownView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
