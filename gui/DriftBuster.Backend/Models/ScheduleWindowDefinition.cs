namespace DriftBuster.Backend.Models;

/// <summary>A daily window: <c>HH:mm</c> or <c>HH:mm:ss</c> bounds in a time zone (UTC when none); a start after the end runs overnight.</summary>
public sealed record ScheduleWindowDefinition
{
    public required string Start { get; init; }

    public required string End { get; init; }

    public string? Timezone { get; init; }
}
