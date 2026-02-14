namespace DriftBuster.Gui.ViewModels
{
    public static class CatalogSortColumns
    {
        public const string Config = nameof(ConfigCatalogItemViewModel.DisplayName);
        public const string Drift = nameof(ConfigCatalogItemViewModel.DriftCount);
        public const string Coverage = nameof(ConfigCatalogItemViewModel.CoverageRatio);
        public const string Format = nameof(ConfigCatalogItemViewModel.Format);
        public const string Severity = nameof(ConfigCatalogItemViewModel.SeverityRank);
        public const string Updated = nameof(ConfigCatalogItemViewModel.LastUpdated);
    }
}
