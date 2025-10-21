using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DriftBuster.Gui.Views
{
    public partial class ToastHost : UserControl
    {
        public ToastHost()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
