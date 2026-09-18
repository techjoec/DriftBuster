using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DriftBuster.Gui.Views
{
    public partial class CompareView : UserControl
    {
        public CompareView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
