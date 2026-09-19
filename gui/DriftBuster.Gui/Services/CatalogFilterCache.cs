namespace DriftBuster.Gui.Services
{
    public sealed record CatalogFilterCache
    {
        public string? Coverage { get; init; }

        public string? Severity { get; init; }

        public string? Format { get; init; }

        public string? Drift { get; init; }

        public string? Search { get; init; }
    }
}
