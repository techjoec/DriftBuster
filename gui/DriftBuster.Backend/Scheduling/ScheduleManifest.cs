using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Scheduling;

/// <summary><c>schedules.json</c>: the schedules in registration order.</summary>
public sealed record ScheduleManifest(IReadOnlyList<ScheduleDefinition> Schedules);
