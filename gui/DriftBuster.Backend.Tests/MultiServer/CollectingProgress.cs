using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Tests.MultiServer;

/// <summary>Records every progress update on the reporting thread (the test stand-in for the NDJSON stdout buffer).</summary>
internal sealed class CollectingProgress : IProgress<ScanProgress>
{
    public List<ScanProgress> Updates { get; } = [];

    public void Report(ScanProgress value) => Updates.Add(value);
}
