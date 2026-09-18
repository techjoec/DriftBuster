using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views
{
    /// <summary>
    /// Builds the settings table's columns (the setting, then one per server) from the view model's column labels, hides the
    /// file list when there is a single file, and keeps the selected difference in view.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public partial class CompareView : UserControl
    {
        private const double FileListWidth = 320;
        private CompareViewModel? _viewModel;

        public CompareView()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => Watch(DataContext as CompareViewModel);
            AttachedToVisualTree += (_, _) => Watch(DataContext as CompareViewModel);
            DetachedFromVisualTree += (_, _) => Watch(null);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void Watch(CompareViewModel? viewModel)
        {
            if (ReferenceEquals(viewModel, _viewModel))
            {
                return;
            }

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelChanged;
                _viewModel.Columns.CollectionChanged -= OnColumnsChanged;
            }

            _viewModel = viewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelChanged;
                _viewModel.Columns.CollectionChanged += OnColumnsChanged;
            }

            BuildColumns();
            UpdateFileList();
        }

        private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => BuildColumns();

        private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(CompareViewModel.ShowFileList):
                    UpdateFileList();
                    break;
                case nameof(CompareViewModel.SelectedRow):
                    BringSelectedRowIntoView();
                    break;
            }
        }

        private void UpdateFileList()
        {
            var show = _viewModel?.ShowFileList ?? false;
            if (this.FindControl<Grid>("Body") is { } body)
            {
                body.ColumnDefinitions[0].Width = new GridLength(show ? FileListWidth : 0);
                body.ColumnDefinitions[1].Width = new GridLength(show ? 6 : 0);
            }

            if (this.FindControl<Control>("FileListPane") is { } pane)
            {
                pane.IsVisible = show;
            }

            if (this.FindControl<Control>("FileListSplitter") is { } splitter)
            {
                splitter.IsVisible = show;
            }
        }

        private void BringSelectedRowIntoView()
        {
            if (_viewModel?.SelectedRow is not { } row || this.FindControl<DataGrid>("SettingsGrid") is not { } grid)
            {
                return;
            }

            // The grid may still be swapping to the newly selected file's rows; scroll once it has, a few rows past the setting
            // first so it lands with context below it.
            var rows = _viewModel.SelectedFile?.VisibleRows;
            Dispatcher.UIThread.Post(() =>
            {
                if (rows is not null)
                {
                    var index = -1;
                    for (var position = 0; position < rows.Count; position++)
                    {
                        if (ReferenceEquals(rows[position], row))
                        {
                            index = position;
                            break;
                        }
                    }

                    if (index >= 0)
                    {
                        grid.ScrollIntoView(rows[Math.Min(index + 5, rows.Count - 1)], null);
                    }
                }

                grid.ScrollIntoView(row, null);
            }, DispatcherPriority.Background);
        }

        private void BuildColumns()
        {
            if (this.FindControl<DataGrid>("SettingsGrid") is not { } grid)
            {
                return;
            }

            grid.Columns.Clear();
            if (_viewModel is null)
            {
                return;
            }

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Setting",
                Width = new DataGridLength(2, DataGridLengthUnitType.Star),
                MinWidth = 180,
                CellTemplate = new FuncDataTemplate<CompareRowViewModel>((_, _) =>
                {
                    var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(8, 5) };
                    text.DataContextChanged += (_, _) => text.Text = (text.DataContext as CompareRowViewModel)?.Key;
                    return text;
                }, supportsRecycling: true),
            });

            for (var index = 0; index < _viewModel.Columns.Count; index++)
            {
                var column = index;
                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = _viewModel.Columns[index],
                    Width = new DataGridLength(3, DataGridLengthUnitType.Star),
                    MinWidth = 160,
                    CellTemplate = new FuncDataTemplate<CompareRowViewModel>((_, _) => BuildCell(column), supportsRecycling: true),
                });
            }
        }

        private static Border BuildCell(int column)
        {
            var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
            var border = new Border { Child = text };
            border.Classes.Add("cell");
            border.DataContextChanged += (_, _) =>
            {
                var row = border.DataContext as CompareRowViewModel;
                var cell = row is not null && column < row.Cells.Count ? row.Cells[column] : null;
                text.Text = cell?.Text;
                text.Classes.Set("absent", cell?.IsAbsent == true);
                border.Classes.Set("differs", cell?.IsDifferent == true);
                AutomationProperties.SetName(text, cell?.AutomationName ?? string.Empty);
            };
            return border;
        }
    }
}
