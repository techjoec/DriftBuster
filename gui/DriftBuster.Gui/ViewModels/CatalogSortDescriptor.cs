namespace DriftBuster.Gui.ViewModels
{
    public sealed record CatalogSortDescriptor(string ColumnKey, bool Descending)
    {
        public static CatalogSortDescriptor Default { get; } = new(CatalogSortColumns.Updated, true);

        public static CatalogSortDescriptor Normalize(string? columnKey, bool descending)
        {
            return columnKey?.Trim() switch
            {
                CatalogSortColumns.Drift => new CatalogSortDescriptor(CatalogSortColumns.Drift, descending),
                CatalogSortColumns.Coverage => new CatalogSortDescriptor(CatalogSortColumns.Coverage, descending),
                CatalogSortColumns.Format => new CatalogSortDescriptor(CatalogSortColumns.Format, descending),
                CatalogSortColumns.Severity => new CatalogSortDescriptor(CatalogSortColumns.Severity, descending),
                CatalogSortColumns.Updated => new CatalogSortDescriptor(CatalogSortColumns.Updated, descending),
                CatalogSortColumns.Config => new CatalogSortDescriptor(CatalogSortColumns.Config, descending),
                _ => new CatalogSortDescriptor(CatalogSortColumns.Config, descending),
            };
        }
    }
}
