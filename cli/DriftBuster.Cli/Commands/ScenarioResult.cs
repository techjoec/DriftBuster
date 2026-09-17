namespace DriftBuster.Cli.Commands;

/// <summary>
/// One self-check scenario: its name, the value its judge returned (Python's <c>and</c> chain yields <c>True</c>, <c>False</c>, or the
/// first falsy operand) and the evaluated response.
/// </summary>
internal sealed record ScenarioResult(string Name, object? Passed, OrderedDictionary<string, object?> Details);
