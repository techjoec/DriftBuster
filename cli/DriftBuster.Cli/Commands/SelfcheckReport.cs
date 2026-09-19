namespace DriftBuster.Cli.Commands;

/// <summary>The self-check report file.</summary>
internal sealed record SelfcheckReport(DateTimeOffset GeneratedAt, string SamplesRoot, int Passed, int Total, IReadOnlyList<ScenarioResult> Scenarios)
{
    public bool Success => Passed == Total;
}
