namespace DriftBuster.Backend.Tests.Reporting;

/// <summary>
/// Test classes that swap the process-wide <c>HtmlReport.UtcNow</c> and <c>SnapshotManifest.UtcNow</c> seams (as the oracle generator
/// replaces <c>datetime</c> in the Python modules) run one at a time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ReportingSeamCollection
{
    public const string Name = "reporting-seams";
}
