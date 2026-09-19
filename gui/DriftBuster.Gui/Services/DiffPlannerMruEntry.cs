using System;
using System.Collections.Generic;

namespace DriftBuster.Gui.Services
{
    /// <summary>A file set the Diff planner compared: the baseline, the files compared with it, and when.</summary>
    public sealed record DiffPlannerMruEntry
    {
        public required string BaselinePath { get; init; }

        public IReadOnlyList<string> ComparisonPaths { get; init => field = value ?? []; } = [];

        public string? DisplayName { get; init; }

        public DateTimeOffset LastUsedUtc { get; init; }

        public DiffPlannerPayloadKind PayloadKind { get; init; }

        public string? SanitizedDigest { get; init; }
    }
}
