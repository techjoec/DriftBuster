using System.Collections.Generic;

namespace DriftBuster.Gui.Services
{
    /// <summary>The Diff planner's recent file sets, newest first: <c>cache/diff-planner/mru.json</c> under the data root.</summary>
    public sealed record DiffPlannerMruSnapshot
    {
        public int SchemaVersion { get; init; } = DiffPlannerMruStore.CurrentSchemaVersion;

        public IReadOnlyList<DiffPlannerMruEntry> Entries { get; init => field = value ?? []; } = [];
    }
}
