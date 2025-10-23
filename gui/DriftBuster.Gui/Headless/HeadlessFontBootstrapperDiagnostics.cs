using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DriftBuster.Gui.Headless;

internal static class HeadlessFontBootstrapperDiagnostics
{
    private static readonly object Sync = new();
    private static ProbeSnapshot _snapshot = ProbeSnapshot.Empty;

    public static void RecordSnapshot(ProbeSnapshot snapshot)
    {
        lock (Sync)
        {
            _snapshot = snapshot;
        }
    }

    public static ProbeSnapshot GetSnapshot()
    {
        lock (Sync)
        {
            return _snapshot;
        }
    }

    internal readonly record struct ProbeSnapshot(
        DateTimeOffset Timestamp,
        IReadOnlyList<ProbeResult> Probes,
        bool ResourceContainsSystemFonts,
        bool ResourceContainsInter,
        int ResourceCount,
        string? FailureNotes)
    {
        public static readonly ProbeSnapshot Empty = new(
            DateTimeOffset.MinValue,
            Array.Empty<ProbeResult>(),
            ResourceContainsSystemFonts: false,
            ResourceContainsInter: false,
            ResourceCount: 0,
            FailureNotes: null);
    }

    internal readonly record struct ProbeResult(
        string Alias,
        bool Success,
        string? GlyphFamily,
        string? Error)
    {
        public IReadOnlyDictionary<string, string?> ToDictionary()
            => new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["alias"] = Alias,
                ["success"] = Success ? "true" : "false",
                ["glyph_family"] = GlyphFamily,
                ["error"] = Error,
            });
    }
}
