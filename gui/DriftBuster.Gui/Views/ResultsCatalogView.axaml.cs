using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using DriftBuster.Gui.ViewModels;

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

        // Double-clicking a row opens that file's details, the same as its button.
        private void OnCatalogGridDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (DataContext is ResultsCatalogViewModel viewModel
                && sender is DataGrid { SelectedItem: ConfigCatalogItemViewModel item }
                && viewModel.DrilldownCommand.CanExecute(item))
            {
                viewModel.DrilldownCommand.Execute(item);
            }
        }

        // Right-click a file: see its settings in Compare, open its details, or any of Compare's file actions (group, rule,
        // ignore the source, copy the path, report a bug).
        private void OnCatalogGridContextRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
        {
            if (DataContext is not ResultsCatalogViewModel viewModel
                || (e.Source as Control)?.DataContext is not ConfigCatalogItemViewModel item
                || sender is not DataGrid grid)
            {
                return;
            }

            grid.SelectedItem = item;
            MenuItem Item(string header, Action action)
            {
                var menuItem = new MenuItem { Header = header };
                menuItem.Click += (_, _) => action();
                return menuItem;
            }

            var leading = new List<Control>
            {
                Item("Show settings in Compare", () => viewModel.RequestCompare(item)),
                Item("File details", () => viewModel.DrilldownCommand.Execute(item)),
                new Separator(),
            };
            var menu = viewModel.Compare?.FileFor(item.ConfigId) is { } file
                ? CompareContextMenu.Build(this, viewModel.Compare, new CompareContext(file), leading)
                : new ContextMenu { ItemsSource = leading.Take(2).ToArray() };
            grid.ContextMenu = menu;
            menu.Open(grid);
            e.Handled = true;
        }

        private void OnCatalogGridSorting(object? sender, DataGridColumnEventArgs e)
        {
            if (DataContext is not ResultsCatalogViewModel viewModel)
            {
                return;
            }

            var columnKey = e.Column.SortMemberPath ?? e.Column.Header?.ToString() ?? CatalogSortColumns.Config;
            var current = viewModel.SortDescriptor;
            var nextDescending = string.Equals(current.ColumnKey, columnKey, StringComparison.OrdinalIgnoreCase)
                ? !current.Descending
                : false;

            viewModel.SetSortDescriptor(columnKey, nextDescending);
            e.Handled = true;
        }
    }
}
