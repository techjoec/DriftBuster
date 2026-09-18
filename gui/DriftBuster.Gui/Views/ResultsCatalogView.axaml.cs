using System;

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
