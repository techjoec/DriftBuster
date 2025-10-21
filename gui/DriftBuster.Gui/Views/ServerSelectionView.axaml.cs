using System;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views
{
    public partial class ServerSelectionView : UserControl
    {
        private ServerSelectionViewModel? _viewModel;

        public ServerSelectionView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_viewModel is not null)
            {
                _viewModel.CopyActivityRequested -= OnCopyActivityRequested;
            }

            _viewModel = DataContext as ServerSelectionViewModel;
            if (_viewModel is not null)
            {
                _viewModel.CopyActivityRequested += OnCopyActivityRequested;
            }
        }

        private async void OnCopyActivityRequested(object? sender, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
            }
        }
    }
}
