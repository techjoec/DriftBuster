using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Scheduling;

using static DriftBuster.Backend.Tests.Scheduling.ScheduleOracleData;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>
/// <c>datetime.fromisoformat</c>, <c>parse_interval</c>, <c>_parse_time</c> and <c>_build_timezone</c> against CPython 3.13 on the inputs in
/// <c>Data/schedule_cases.json</c>: adversarial ISO strings (separators, week dates, fractions, offsets, surrogates, NUL, random
/// mutations), interval tokens and numbers (float rounding, overflow, NaN and infinity), clock times and time zone keys.
/// </summary>
public sealed class ScheduleParsingParityTests
{
    private static void AssertAll(string section, Func<OrderedDictionary<string, object?>, string?> check)
    {
        var failures = new List<string>();
        foreach (var entry in Cases(section))
        {
            if (check(entry) is { } failure)
            {
                failures.Add($"{PythonRepr.Repr(entry.GetValueOrDefault("input"))}: {failure}");
            }
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public void FromIsoFormatMatchesCPython()
        => AssertAll("fromisoformat", entry => Mismatch(entry, () => PythonDateTime.FromIsoFormat((string)entry["input"]!), DateTimeMismatch));

    [Fact]
    public void ParseIntervalMatchesCPython()
        => AssertAll("parse_interval", entry => Mismatch(entry, () => ScheduleParsing.ParseInterval(entry["input"]), DeltaMismatch));

    [Fact]
    public void ParseTimeMatchesCPython()
        => AssertAll("parse_time", entry => Mismatch(
            entry,
            () => ScheduleParsing.ParseTime((string)entry["input"]!).IsoFormat(),
            (actual, expected) => string.Equals(actual, (string)expected!, StringComparison.Ordinal) ? null : $"got {actual}"));

    [Fact]
    public void BuildTimezoneMatchesCPython()
        => AssertAll("build_timezone", entry => Mismatch(
            entry,
            () => ScheduleParsing.BuildTimezone((string)entry["input"]!).Name,
            (actual, expected) => string.Equals(actual, (string)expected!, StringComparison.Ordinal) ? null : $"got {actual}"));

    [Fact]
    public void TimeDeltaReprAndTotalSecondsMatchCPython()
    {
        PythonTimeDelta.FromMicroseconds(0).Repr().Should().Be("datetime.timedelta(0)");
        PythonTimeDelta.FromMicroseconds(-1).Repr().Should().Be("datetime.timedelta(days=-1, seconds=86399, microseconds=999999)");
        PythonTimeDelta.FromMicroseconds(-1).TotalSeconds().Should().Be(-1e-06);
        (PythonTimeDelta.FromFloats(days: 1) + PythonTimeDelta.FromFloats(seconds: 1.5)).Repr()
            .Should().Be("datetime.timedelta(days=1, seconds=1, microseconds=500000)");
        PythonTimeDelta.FromFloats(days: 1).CompareTo(PythonTimeDelta.FromFloats(hours: 24)).Should().Be(0);
        (PythonTimeDelta.FromFloats(minutes: 1) < PythonTimeDelta.FromFloats(seconds: 61)).Should().BeTrue();
        (PythonTimeDelta.FromFloats(minutes: 1) > PythonTimeDelta.FromFloats(seconds: 61)).Should().BeFalse();
        (PythonTimeDelta.FromFloats(minutes: 1) <= PythonTimeDelta.FromFloats(seconds: 60)).Should().BeTrue();
        (PythonTimeDelta.FromFloats(minutes: 1) >= PythonTimeDelta.FromFloats(seconds: 60)).Should().BeTrue();
        var tooSmall = () => PythonTimeDelta.FromMicroseconds(new System.Numerics.BigInteger(-86_400_000_000L) * 1_000_000_000L);
        tooSmall.Should().Throw<OverflowException>().WithMessage("days=-1000000000; must have magnitude <= 999999999");
    }

    private static string? DeltaMismatch(PythonTimeDelta actual, object? expected)
    {
        var row = List(expected);
        var matches = actual.Days == Long(row[0])
            && actual.Seconds == Long(row[1])
            && actual.Microseconds == Long(row[2])
            && actual.TotalSeconds().Equals((double)row[3]!)
            && string.Equals(actual.Repr(), (string)row[4]!, StringComparison.Ordinal);
        return matches ? null : $"got {actual.Repr()} ({PythonRepr.Float(actual.TotalSeconds())}), expected {row[4]} ({PythonRepr.Repr(row[3])})";
    }
}
