using System;

namespace DriftBuster.Gui.Services
{
    public sealed record ActivityCacheEntry
    {
        public DateTimeOffset Timestamp { get; init; }

        public required string Severity { get; init; }

        public required string Summary { get; init; }

        public string? Detail { get; init; }

        public string? Category { get; init; }
    }
}
