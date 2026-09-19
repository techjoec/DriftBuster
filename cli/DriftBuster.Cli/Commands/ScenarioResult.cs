namespace DriftBuster.Cli.Commands;

/// <summary>One self-check scenario: whether it passed and what the run looked like.</summary>
internal sealed record ScenarioResult(string Name, bool Passed, string Details);
