using System;
using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views
{
    public partial class ResultsCatalogView : UserControl
    {
        private DataGrid? _catalogGrid;
        private ResultsCatalogViewModel? _viewModel;

        public ResultsCatalogView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            _catalogGrid = this.FindControl<DataGrid>("CatalogGrid");
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_viewModel is not null)
            {
                _viewModel.SortDescriptorChanged -= OnSortDescriptorChanged;
            }

            _viewModel = DataContext as ResultsCatalogViewModel;
            if (_viewModel is not null)
            {
                _viewModel.SortDescriptorChanged += OnSortDescriptorChanged;
                ApplySortDescriptor(_viewModel.SortDescriptor);
            }
        }

        private void OnSortDescriptorChanged(object? sender, CatalogSortDescriptor descriptor)
        {
            ApplySortDescriptor(descriptor);
        }

        private void ApplySortDescriptor(CatalogSortDescriptor descriptor)
        {
            if (_catalogGrid is null)
            {
                return;
            }

            var direction = descriptor.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;

            foreach (var column in _catalogGrid.Columns)
            {
                var columnKey = column.SortMemberPath ?? column.Header?.ToString();
                if (string.Equals(columnKey, descriptor.ColumnKey, StringComparison.OrdinalIgnoreCase))
                {
                    column.Sort(direction);
                }
                else
                {
                    column.ClearSort();
                }
            }
        }

        private void OnCatalogGridSorting(object? sender, DataGridColumnEventArgs e)
        {
            if (_viewModel is null)
            {
                return;
            }

            var columnKey = e.Column.SortMemberPath ?? e.Column.Header?.ToString() ?? CatalogSortColumns.Config;
            var current = _viewModel.SortDescriptor;
            var nextDescending = string.Equals(current.ColumnKey, columnKey, StringComparison.OrdinalIgnoreCase)
                ? !current.Descending
                : false;

            _viewModel.SetSortDescriptor(columnKey, nextDescending);
            e.Handled = true;
        }
    }
}
