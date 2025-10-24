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
