using DriftBuster.Backend.Infrastructure;

using static DriftBuster.Backend.Tests.Scheduling.ScheduleOracleData;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>
/// <see cref="PythonZoneInfo"/> against CPython's <c>zoneinfo</c> for every offset change in 1970, 1971, 2011, 2012, 2024 to 2026 and 2040
/// of zones with ordinary DST, 30-minute DST, negative DST (Europe/Dublin), half-hour and 45-minute offsets, a skipped day (Pacific/Apia),
/// abolished DST and a two-hour shift (Antarctica/Troll), plus the years after 2037 of zones whose TZif footer rule has a transition hour
/// outside 0..24 (Asia/Jerusalem, America/Santiago, Africa/Cairo, America/Nuuk) and the years of offsets with seconds (Africa/Monrovia
/// 1970-1972, America/Santiago 1918 and 1927, Europe/Paris 1911, Eire 1916): <c>astimezone</c> of UTC instants on and around each transition (local fields,
/// fold, offset) and <c>utcoffset</c> / <c>astimezone(UTC)</c> of wall times in and around each gap and fold with fold 0 and 1.
/// </summary>
public sealed class ZoneInfoParityTests
{
    public static TheoryData<string> Zones()
    {
        var zones = new TheoryData<string>();
        foreach (var entry in Cases("zones"))
        {
            zones.Add((string)entry["zone"]!);
        }

        return zones;
    }

    private static OrderedDictionary<string, object?> Zone(string key) => Cases("zones").Single(entry => string.Equals((string)entry["zone"]!, key, StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Zones))]
    public void FromUtcMatchesZoneInfo(string key)
    {
        var zone = PythonZoneInfo.Create(key);
        var failures = new List<string>();
        foreach (var item in List(Zone(key)["fromutc"]))
        {
            var row = List(item);
            var utc = PythonDateTime.FromIsoFormat((string)row[0]!);
            if (DateTimeMismatch(utc.AsTimeZone(zone), row[1]) is { } failure)
            {
                failures.Add($"{row[0]}: {failure}");
            }
        }

        failures.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public void UtcOffsetMatchesZoneInfo(string key)
    {
        var zone = PythonZoneInfo.Create(key);
        var failures = new List<string>();
        foreach (var item in List(Zone(key)["utcoffset"]))
        {
            var row = List(item);
            var local = PythonDateTime.FromIsoFormat((string)row[0]!).WithTz(zone).WithFold((int)Long(row[1]));
            var offset = local.UtcOffset();
            var utc = local.AsTimeZone(PythonFixedOffset.Utc).IsoFormat();
            if (offset != Long(row[2]) * 1_000_000 || !string.Equals(utc, (string)row[3]!, StringComparison.Ordinal))
            {
                failures.Add($"{row[0]} fold={row[1]}: got {offset} {utc}, expected {Long(row[2]) * 1_000_000} {row[3]}");
            }
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public void ZoneInstancesAreCachedPerKey()
        => PythonZoneInfo.Create("America/Chicago").Should().BeSameAs(PythonZoneInfo.Create("America/Chicago"));

    [Fact]
    public void KeysThatAreNotNormalisedRelativePathsRaiseValueError()
    {
        FluentActions.Invoking(() => PythonZoneInfo.Create("/etc/localtime")).Should().Throw<PythonValueException>()
            .WithMessage("ZoneInfo keys may not be absolute paths, got: /etc/localtime");
        FluentActions.Invoking(() => PythonZoneInfo.Create("America//Chicago")).Should().Throw<PythonValueException>()
            .WithMessage("ZoneInfo keys must be normalized relative paths, got: America//Chicago");
        FluentActions.Invoking(() => PythonZoneInfo.Create("../UTC")).Should().Throw<PythonValueException>()
            .WithMessage("ZoneInfo keys must refer to subdirectories of TZPATH, got: ../UTC");
        FluentActions.Invoking(() => PythonZoneInfo.Create("Nowhere/Special")).Should().Throw<TimeZoneNotFoundException>()
            .WithMessage("No time zone found with key Nowhere/Special");
    }

    [Fact]
    public void ConversionsPastTheCalendarRaiseOverflowError()
    {
        var chicago = PythonZoneInfo.Create("America/Chicago");
        FluentActions.Invoking(() => PythonDateTime.Create(1, 1, 1, 0, 30, tz: PythonFixedOffset.Utc).AsTimeZone(chicago))
            .Should().Throw<OverflowException>().WithMessage("date value out of range");
        FluentActions.Invoking(() => PythonDateTime.Create(9999, 12, 31, 23, tz: PythonFixedOffset.Utc).AsTimeZone(PythonZoneInfo.Create("Asia/Kolkata")))
            .Should().Throw<OverflowException>().WithMessage("date value out of range");
    }
}
