using Avalonia.Markup.Xaml;
using Avalonia.Controls;

namespace DriftBuster.Gui.Views
{
    public partial class ResultsCatalogView : UserControl
    {
        public ResultsCatalogView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
