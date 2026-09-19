namespace DriftBuster.Backend.Scheduling;

/// <summary>A due run of a schedule, with the schedule's tags and metadata.</summary>
public sealed record ScheduledRun(
    string Name,
    string Profile,
    DateTimeOffset ScheduledFor,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Metadata);
