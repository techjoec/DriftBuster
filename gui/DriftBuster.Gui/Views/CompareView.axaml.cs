using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
                _viewModel.DifferenceSelected -= OnDifferenceSelected;
            }

            _viewModel = viewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelChanged;
                _viewModel.Columns.CollectionChanged += OnColumnsChanged;
                _viewModel.DifferenceSelected += OnDifferenceSelected;
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

        // Only navigation scrolls: a row the user clicks or right-clicks is already where they are looking.
        private void OnDifferenceSelected(object? sender, EventArgs e) => BringSelectedRowIntoView();

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
                CellTemplate = new FuncDataTemplate<CompareRowViewModel>((_, _) => BuildSettingCell(), supportsRecycling: true),
            });

            for (var index = 0; index < _viewModel.Columns.Count; index++)
            {
                var column = index;
                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = _viewModel.Columns[index],
                    Width = new DataGridLength(3, DataGridLengthUnitType.Star),
                    MinWidth = 160,
                    CellTemplate = new FuncDataTemplate<CompareRowViewModel>((_, _) => BuildValueCell(column), supportsRecycling: true),
                });
            }
        }

        // The setting's name, with its marker, review flag and groups beneath; right-click for the menu.
        private Border BuildSettingCell()
        {
            var key = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var flags = new TextBlock { TextWrapping = TextWrapping.Wrap };
            flags.Classes.Add("row-flags");
            var border = new Border { Child = new StackPanel { Children = { key, flags } }, Padding = new Avalonia.Thickness(8, 5), Background = Brushes.Transparent };
            CompareRowViewModel? shown = null;
            void Update()
            {
                var row = border.DataContext as CompareRowViewModel;
                key.Text = row is null ? null : (row.IsMarked ? "★ " : string.Empty) + row.Key;
                var parts = new List<string>();
                if (row?.InReview == true)
                {
                    parts.Add("⚑ review");
                }

                if (row?.Ignored == true)
                {
                    parts.Add("ignored");
                }

                if (row is { Groups.Count: > 0 })
                {
                    parts.Add(string.Join(", ", row.Groups));
                }

                flags.Text = string.Join("  ·  ", parts);
                flags.IsVisible = parts.Count > 0;
                border.Classes.Set("ignored", row?.Ignored == true);
            }

            void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Update();
            border.DataContextChanged += (_, _) =>
            {
                if (shown is not null)
                {
                    shown.PropertyChanged -= OnRowChanged;
                }

                shown = border.DataContext as CompareRowViewModel;
                if (shown is not null)
                {
                    shown.PropertyChanged += OnRowChanged;
                }

                Update();
            };
            border.ContextRequested += (_, e) => OpenMenu(border, e, border.DataContext as CompareRowViewModel, null);
            return border;
        }

        private Border BuildValueCell(int column)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var border = new Border { Child = text };
            border.Classes.Add("cell");
            border.DataContextChanged += (_, _) =>
            {
                var row = border.DataContext as CompareRowViewModel;
                var cell = row is not null && column < row.Cells.Count ? row.Cells[column] : null;
                text.Text = cell?.Text;
                text.Classes.Set("absent", cell?.IsAbsent == true);
                border.Classes.Set("differs", cell?.IsDifferent == true && !cell.IsIgnored && row?.Ignored == false);
                border.Classes.Set("ignored", cell?.IsIgnored == true || row?.Ignored == true);
                AutomationProperties.SetName(text, cell?.AutomationName ?? string.Empty);
            };
            border.ContextRequested += (_, e) =>
            {
                var row = border.DataContext as CompareRowViewModel;
                OpenMenu(border, e, row, row is not null && column < row.Cells.Count ? row.Cells[column] : null);
            };
            return border;
        }

        private void OpenMenu(Control target, ContextRequestedEventArgs e, CompareRowViewModel? row, CompareCellViewModel? cell)
        {
            if (_viewModel?.SelectedFile is not { } file || row is null)
            {
                return;
            }

            _viewModel.SelectedRow = row;
            var menu = CompareContextMenu.Build(this, _viewModel, new CompareContext(file, row, cell));
            target.ContextMenu = menu;
            menu.Open(target);
            e.Handled = true;
        }

        private void OnFileContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            if (_viewModel is null || sender is not Control { DataContext: CompareFileViewModel file } target)
            {
                return;
            }

            var menu = CompareContextMenu.Build(this, _viewModel, new CompareContext(file));
            target.ContextMenu = menu;
            menu.Open(target);
            e.Handled = true;
        }

        private async void OnManageChoices(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_viewModel is not null)
            {
                await DialogHost.ShowManagerAsync(this, _viewModel, new CurationManagerViewModel(_viewModel.Curation, _viewModel.HostSetId)).ConfigureAwait(true);
            }
        }
    }
}
