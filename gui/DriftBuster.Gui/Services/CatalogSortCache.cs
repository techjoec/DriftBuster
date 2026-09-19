namespace DriftBuster.Gui.Services
{
    public sealed record CatalogSortCache
    {
        public required string Column { get; init; }

        public bool Descending { get; init; }
    }
}
