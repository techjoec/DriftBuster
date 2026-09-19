using System.Diagnostics.CodeAnalysis;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views
{
    /// <summary>A line diff with previous/next change; keeps the selected change in view.</summary>
    [ExcludeFromCodeCoverage]
    public partial class DiffLinesView : UserControl
    {
        private const int ContextBelow = 8;

        private DiffLinesViewModel? _viewModel;

        public DiffLinesView()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => Watch(DataContext as DiffLinesViewModel);
            AttachedToVisualTree += (_, _) => Watch(DataContext as DiffLinesViewModel);
            DetachedFromVisualTree += (_, _) => Watch(null);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void Watch(DiffLinesViewModel? viewModel)
        {
            if (ReferenceEquals(viewModel, _viewModel))
            {
                return;
            }

            if (_viewModel is not null)
            {
                _viewModel.ChangeSelected -= OnChangeSelected;
            }

            _viewModel = viewModel;
            if (_viewModel is not null)
            {
                _viewModel.ChangeSelected += OnChangeSelected;
            }
        }

        // Only navigation scrolls: a line the user clicks is already where they are looking.
        private void OnChangeSelected(object? sender, System.EventArgs e)
        {
            if (_viewModel?.SelectedLine is not { } line)
            {
                return;
            }

            var list = this.FindControl<ListBox>(_viewModel.IsSplit ? "SplitLines" : "UnifiedLines");
            var lines = _viewModel.Lines;
            Dispatcher.UIThread.Post(() =>
            {
                if (list is null)
                {
                    return;
                }

                // Scroll a few lines past the change first so it lands with context below it, not on the bottom edge.
                var index = IndexOf(lines, line);
                if (index >= 0)
                {
                    list.ScrollIntoView(Math.Min(index + ContextBelow, lines.Count - 1));
                }

                list.ScrollIntoView(line);
            }, DispatcherPriority.Background);
        }

        private static int IndexOf(IReadOnlyList<DiffLineViewModel> lines, DiffLineViewModel line)
        {
            for (var index = 0; index < lines.Count; index++)
            {
                if (ReferenceEquals(lines[index], line))
                {
                    return index;
                }
            }

            return -1;
        }
    }
}
