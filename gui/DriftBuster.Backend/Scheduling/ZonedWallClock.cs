namespace DriftBuster.Backend.Scheduling;

/// <summary>
/// Turns a wall-clock date and time in a <see cref="TimeZoneInfo"/> into a UTC instant, for the scheduler's window alignment.
/// <list type="bullet">
/// <item>A wall time that occurs once is that instant.</item>
/// <item>A wall time skipped by a forward change (<see cref="TimeZoneInfo.IsInvalidTime"/>) uses the offset in force before the
/// change, so it lands the length of the gap later on the clock: America/Chicago 2025-03-09 02:15 is 08:15Z (03:15 CDT).</item>
/// <item>A wall time repeated by a backward change (<see cref="TimeZoneInfo.IsAmbiguousTime(DateTime)"/>) is its earlier occurrence,
/// or, when a lower bound is given, the earliest occurrence not before that bound.</item>
/// </list>
/// </summary>
internal static class ZonedWallClock
{
    public static DateTimeOffset ToInstant(DateTime wallClock, TimeZoneInfo zone, DateTimeOffset? notBefore = null)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            return AsUtc(local, OffsetBeforeGap(local, zone));
        }

        if (zone.IsAmbiguousTime(local))
        {
            var occurrences = zone.GetAmbiguousTimeOffsets(local)
                .Select(offset => AsUtc(local, offset))
                .Order()
                .ToList();
            if (notBefore is { } bound)
            {
                var index = occurrences.FindIndex(instant => instant >= bound);
                return occurrences[index < 0 ? occurrences.Count - 1 : index];
            }

            return occurrences[0];
        }

        return AsUtc(local, zone.GetUtcOffset(local));
    }

    private static DateTimeOffset AsUtc(DateTime local, TimeSpan offset) => new DateTimeOffset(local, offset).ToUniversalTime();

    // A gap follows a change a few hours around the wall time; one day earlier (read as UTC) is before that change for any offset.
    private static TimeSpan OffsetBeforeGap(DateTime local, TimeZoneInfo zone)
    {
        var earlier = local > DateTime.MinValue.AddDays(1) ? local.AddDays(-1) : DateTime.MinValue;
        return zone.GetUtcOffset(new DateTimeOffset(DateTime.SpecifyKind(earlier, DateTimeKind.Unspecified), TimeSpan.Zero));
    }
}
