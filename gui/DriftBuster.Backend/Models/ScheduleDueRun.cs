namespace DriftBuster.Backend.Models;

/// <summary>One run <c>schedule due</c> hands out.</summary>
public sealed record ScheduleDueRun(
    string Name,
    string Profile,
    DateTimeOffset ScheduledFor,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Metadata);
