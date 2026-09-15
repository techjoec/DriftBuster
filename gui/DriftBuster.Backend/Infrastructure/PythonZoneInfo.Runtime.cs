namespace DriftBuster.Backend.Infrastructure;

/// <remarks>
/// Windows: the offsets come from <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> for UTC instants, and <see cref="RuntimeUtcOffset"/>
/// and <see cref="RuntimeFromUtc"/> rebuild zoneinfo's transition rules from them, so gaps and folds resolve as PEP 495 and
/// <c>_zoneinfo.c</c> resolve them: at a transition at UTC second <c>t</c> from offset <c>b</c> to <c>a</c>, a local time <c>L</c> takes
/// <c>a</c> when <c>L &gt;= t + max(a, b)</c> with fold 0, or <c>L &gt;= t + min(a, b)</c> with fold 1; and a UTC instant <c>u</c> after the
/// last transition <c>t</c> gets fold 1 when <c>b - a &gt; u - t</c>. Transitions are located by probing the offset every hour across a
/// window wider than any UTC offset, then bisecting to the second; two offset changes less than an hour apart are seen as one. The runtime
/// rounds offsets with seconds to whole minutes, and its rules are the runtime's.
/// </remarks>
public sealed partial class PythonZoneInfo
{
    private const long WindowSeconds = 26 * 3600;
    private const long ProbeSeconds = 3600;

    private static readonly long MaxSecond = DateTime.MaxValue.Ticks / TimeSpan.TicksPerSecond;

    private readonly TimeZoneInfo? _runtime;

    private static TimeZoneInfo FindRuntimeZone(string key)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(key);
        }
        catch (Exception exc) when (exc is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw NotFound(key);
        }

        return zone.HasIanaId ? zone : throw NotFound(key);
    }

    // The offset of the last transition whose fold-dependent wall time is at or before the local second, in seconds.
    private long RuntimeUtcOffset(PythonDateTime moment)
    {
        var local = moment.FieldSeconds;
        var offset = OffsetAt(local - WindowSeconds);
        foreach (var (at, before, after) in Transitions(local - WindowSeconds, local + WindowSeconds))
        {
            var wall = at + (moment.Fold == 0 ? Math.Max(before, after) : Math.Min(before, after));
            if (local >= wall)
            {
                offset = after;
            }
        }

        return offset;
    }

    private PythonDateTime RuntimeFromUtc(PythonDateTime moment)
    {
        var utc = moment.FieldSeconds;
        var transitions = Transitions(utc - WindowSeconds, utc);
        if (transitions.Count == 0)
        {
            return moment.AddMicroseconds(OffsetAt(utc) * MicrosecondsPerSecond);
        }

        var (at, before, after) = transitions[^1];
        var shifted = moment.AddMicroseconds(after * MicrosecondsPerSecond);
        return before - after > utc - at ? shifted.WithFold(1) : shifted;
    }

    // The offset in seconds at a UTC second counted from 0001-01-01T00:00:00, clamped to the calendar's range.
    private long OffsetAt(long utcSecond)
    {
        var clamped = Math.Clamp(utcSecond, 0, MaxSecond);
        var offset = _runtime!.GetUtcOffset(new DateTime(clamped * TimeSpan.TicksPerSecond, DateTimeKind.Utc));
        return offset.Ticks / TimeSpan.TicksPerSecond;
    }

    // Every offset change in (from, to], ascending: the first UTC second of the new offset, the offset before and the offset after.
    private List<(long At, long Before, long After)> Transitions(long from, long to)
    {
        var found = new List<(long, long, long)>();
        var cursor = from;
        var offset = OffsetAt(cursor);
        while (cursor < to)
        {
            var next = Math.Min(cursor + ProbeSeconds, to);
            if (OffsetAt(next) == offset)
            {
                cursor = next;
                continue;
            }

            var low = cursor;
            var high = next;
            while (high - low > 1)
            {
                var middle = low + ((high - low) / 2);
                if (OffsetAt(middle) == offset)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            var after = OffsetAt(high);
            found.Add((high, offset, after));
            cursor = high;
            offset = after;
        }

        return found;
    }
}
