using System.Collections.Generic;

namespace DriftBuster.Gui.Services
{
    /// <summary>The Multi-server page as it was left: <c>sessions/multi-server.json</c> under the data root.</summary>
    public sealed record ServerSelectionCache
    {
        // Source-generated reads pass default for an absent init-only member, so the setters restore the empty defaults.
        public int SchemaVersion { get; init; } = SessionCacheService.CurrentSchemaVersion;

        public bool PersistSession { get; init; }

        public IReadOnlyList<ServerSelectionCacheEntry> Servers { get; init => field = value ?? []; } = [];

        public IReadOnlyList<string> SharedRegistryKeys { get; init => field = value ?? []; } = [];

        public IReadOnlyList<ActivityCacheEntry> Activities { get; init => field = value ?? []; } = [];

        public CatalogSortCache? CatalogSort { get; init; }

        public CatalogFilterCache? CatalogFilters { get; init; }

        public ActivityTimelineCache? Timeline { get; init; }

        public string? ActiveView { get; init; }
    }
}
