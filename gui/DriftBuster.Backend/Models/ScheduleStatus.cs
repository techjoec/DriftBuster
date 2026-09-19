namespace DriftBuster.Backend.Models;

/// <summary>One entry of <c>schedule list</c>: a schedule from the manifest with its scheduler state.</summary>
public sealed record ScheduleStatus(
    string Name,
    string Profile,
    double IntervalSeconds,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset? StartAt,
    DateTimeOffset NextRun,
    DateTimeOffset? Pending,
    ScheduleWindowDefinition? Window);
