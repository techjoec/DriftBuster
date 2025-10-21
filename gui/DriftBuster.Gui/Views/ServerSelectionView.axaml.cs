using Avalonia.Markup.Xaml;
using Avalonia.Controls;

namespace DriftBuster.Gui.Views
{
    public partial class ServerSelectionView : UserControl
    {
        public ServerSelectionView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
