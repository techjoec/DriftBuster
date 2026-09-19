namespace DriftBuster.Backend.Models;

/// <summary>
/// One schedule in <c>schedules.json</c>: run <see cref="Profile"/> every <see cref="Every"/> (<c>15m</c>, <c>1h30m</c>, <c>1.5d</c>
/// or an ISO 8601 time duration such as <c>PT2H</c>), from an optional ISO 8601 <see cref="StartAt"/> (UTC when it has no offset),
/// inside an optional daily <see cref="Window"/>.
/// </summary>
public sealed record ScheduleDefinition
{
    public required string Name { get; init; }

    public required string Profile { get; init; }

    public required string Every { get; init; }

    public string? StartAt { get; init; }

    public ScheduleWindowDefinition? Window { get; init; }

    // Source-generated reads pass null for an absent init-only member, so the setters restore the empty default.
    public IReadOnlyList<string> Tags { get; init => field = value ?? []; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init => field = value ?? Empty; } = Empty;

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(StringComparer.Ordinal);
}
