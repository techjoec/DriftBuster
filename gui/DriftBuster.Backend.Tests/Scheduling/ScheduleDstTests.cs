using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Scheduling;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>
/// Windows, wall-clock offsets and schedule chains across daylight saving changes in America/Chicago (the 2025-03-09 spring-forward gap
/// and the 2025-11-02 fall-back fold), Europe/London, Australia/Lord_Howe (30-minute DST), Asia/Kolkata (no DST) and UTC. Every expected
/// value is what CPython 3.13's <c>driftbuster.scheduler</c> and <c>zoneinfo</c> return for the same inputs.
/// </summary>
public sealed class ScheduleDstTests
{
    [Theory]
    // A wall time in the gap with fold 0 takes the offset before it, so 02:00 CST is 03:00 CDT and 02:15 lands past a 02:30 end.
    [InlineData("America/Chicago", "02:00", "02:30", "2025-03-09T07:30:00+00:00", "2025-03-09T08:00:00+00:00", false)]
    [InlineData("America/Chicago", "02:00", "02:30", "2025-03-09T08:15:00+00:00", "2025-03-10T07:00:00+00:00", false)]
    [InlineData("America/Chicago", "02:30", "03:30", "2025-03-09T07:45:00+00:00", "2025-03-09T08:30:00+00:00", false)]
    [InlineData("America/Chicago", "22:00", "02:30", "2025-03-09T07:59:59.500000+00:00", "2025-03-09T07:59:59+00:00", true)]
    // Inside the fold: the second 01:10 is in a 01:00-01:59 window; moving to the start keeps fold 1, so 01:30 is the CST one.
    [InlineData("America/Chicago", "01:00", "01:59", "2025-11-02T07:10:00+00:00", "2025-11-02T07:10:00+00:00", true)]
    [InlineData("America/Chicago", "01:00", "01:59", "2025-11-02T08:10:00+00:00", "2025-11-03T07:00:00+00:00", false)]
    [InlineData("America/Chicago", "01:30", "03:00", "2025-11-02T07:00:00+00:00", "2025-11-02T07:30:00+00:00", false)]
    [InlineData("America/Chicago", "01:30", "03:00", "2025-11-02T06:00:00+00:00", "2025-11-02T06:30:00+00:00", false)]
    [InlineData("Europe/London", "01:15", "01:45", "2025-03-30T00:30:00+00:00", "2025-03-30T01:15:00+00:00", false)]
    [InlineData("Europe/London", "01:15", "01:45", "2025-10-26T00:50:00+00:00", "2025-10-27T01:15:00+00:00", false)]
    [InlineData("Europe/London", "01:15", "01:45", "2025-10-26T01:05:00+00:00", "2025-10-26T01:15:00+00:00", false)]
    [InlineData("Europe/London", "23:00", "01:30", "2025-10-26T01:40:00+00:00", "2025-10-26T23:00:00+00:00", false)]
    [InlineData("Australia/Lord_Howe", "01:45", "02:15", "2025-04-05T14:40:00+00:00", "2025-04-05T14:45:00+00:00", false)]
    [InlineData("Australia/Lord_Howe", "01:45", "02:15", "2025-04-05T15:05:00+00:00", "2025-04-05T15:15:00+00:00", false)]
    [InlineData("Australia/Lord_Howe", "02:00", "02:30", "2025-10-04T15:00:00+00:00", "2025-10-04T15:30:00+00:00", false)]
    [InlineData("Australia/Lord_Howe", "02:00", "02:30", "2025-10-04T15:40:00+00:00", "2025-10-05T15:00:00+00:00", false)]
    [InlineData("Asia/Kolkata", "22:00", "05:30", "2025-03-09T12:00:00+00:00", "2025-03-09T16:30:00+00:00", false)]
    [InlineData("Asia/Kolkata", "22:00", "05:30", "2025-03-09T23:59:00+00:00", "2025-03-09T23:59:00+00:00", true)]
    [InlineData("Asia/Kolkata", "09:00", "17:00", "2025-03-09T12:00:00+00:00", "2025-03-10T03:30:00+00:00", false)]
    [InlineData("UTC", "09:00", "17:00", "2025-03-09T17:00:00.999999+00:00", "2025-03-09T17:00:00+00:00", false)]
    [InlineData("UTC", "09:00", "17:00", "2025-03-09T17:00:01+00:00", "2025-03-10T09:00:00+00:00", false)]
    [InlineData("UTC", "17:00", "09:00", "2025-03-09T12:00:00+00:00", "2025-03-09T17:00:00+00:00", false)]
    public void WindowAlignsAndContainsAcrossTransitions(string zone, string start, string end, string candidate, string aligned, bool contains)
    {
        var window = ScheduleWindow.FromDict(SchedulerParityTests.Payload(("start", start), ("end", end), ("timezone", zone)));
        var moment = PythonDateTime.FromIsoFormat(candidate);

        window.Align(moment).IsoFormat().Should().Be(aligned);
        window.Contains(moment).Should().Be(contains);
    }

    [Theory]
    [InlineData("America/Chicago", "2025-03-09T02:30:00", 0, "2025-03-09T02:30:00-06:00", "2025-03-09T08:30:00+00:00")]
    [InlineData("America/Chicago", "2025-03-09T02:30:00", 1, "2025-03-09T02:30:00-05:00", "2025-03-09T07:30:00+00:00")]
    [InlineData("America/Chicago", "2025-11-02T01:30:00", 0, "2025-11-02T01:30:00-05:00", "2025-11-02T06:30:00+00:00")]
    [InlineData("America/Chicago", "2025-11-02T01:30:00", 1, "2025-11-02T01:30:00-06:00", "2025-11-02T07:30:00+00:00")]
    [InlineData("Europe/London", "2025-03-30T01:30:00", 0, "2025-03-30T01:30:00+00:00", "2025-03-30T01:30:00+00:00")]
    [InlineData("Europe/London", "2025-03-30T01:30:00", 1, "2025-03-30T01:30:00+01:00", "2025-03-30T00:30:00+00:00")]
    [InlineData("Europe/London", "2025-10-26T01:30:00", 0, "2025-10-26T01:30:00+01:00", "2025-10-26T00:30:00+00:00")]
    [InlineData("Europe/London", "2025-10-26T01:30:00", 1, "2025-10-26T01:30:00+00:00", "2025-10-26T01:30:00+00:00")]
    [InlineData("Australia/Lord_Howe", "2025-04-06T01:45:00", 0, "2025-04-06T01:45:00+11:00", "2025-04-05T14:45:00+00:00")]
    [InlineData("Australia/Lord_Howe", "2025-04-06T01:45:00", 1, "2025-04-06T01:45:00+10:30", "2025-04-05T15:15:00+00:00")]
    [InlineData("Australia/Lord_Howe", "2025-10-05T02:15:00", 0, "2025-10-05T02:15:00+10:30", "2025-10-04T15:45:00+00:00")]
    [InlineData("Australia/Lord_Howe", "2025-10-05T02:15:00", 1, "2025-10-05T02:15:00+11:00", "2025-10-04T15:15:00+00:00")]
    [InlineData("Asia/Kolkata", "2025-03-09T02:30:00", 1, "2025-03-09T02:30:00+05:30", "2025-03-08T21:00:00+00:00")]
    [InlineData("UTC", "2025-03-09T02:30:00", 1, "2025-03-09T02:30:00+00:00", "2025-03-09T02:30:00+00:00")]
    public void GapAndFoldWallTimesResolveByFold(string zone, string local, int fold, string isoformat, string utc)
    {
        var moment = PythonDateTime.FromIsoFormat(local).WithTz(PythonZoneInfo.Create(zone)).WithFold(fold);

        moment.IsoFormat().Should().Be(isoformat);
        moment.AsTimeZone(PythonFixedOffset.Utc).IsoFormat().Should().Be(utc);
    }

    [Theory]
    [InlineData("America/Chicago", "2025-11-02T06:30:00+00:00", "2025-11-02T01:30:00-05:00", 0)]
    [InlineData("America/Chicago", "2025-11-02T07:30:00+00:00", "2025-11-02T01:30:00-06:00", 1)]
    [InlineData("America/Chicago", "2025-11-02T07:59:59+00:00", "2025-11-02T01:59:59-06:00", 1)]
    [InlineData("America/Chicago", "2025-11-02T08:00:00+00:00", "2025-11-02T02:00:00-06:00", 0)]
    [InlineData("Europe/London", "2025-10-26T00:59:59+00:00", "2025-10-26T01:59:59+01:00", 0)]
    [InlineData("Europe/London", "2025-10-26T01:00:00+00:00", "2025-10-26T01:00:00+00:00", 1)]
    [InlineData("Australia/Lord_Howe", "2025-04-05T14:59:59+00:00", "2025-04-06T01:59:59+11:00", 0)]
    [InlineData("Australia/Lord_Howe", "2025-04-05T15:00:00+00:00", "2025-04-06T01:30:00+10:30", 1)]
    [InlineData("Australia/Lord_Howe", "2025-04-05T15:29:59+00:00", "2025-04-06T01:59:59+10:30", 1)]
    [InlineData("Australia/Lord_Howe", "2025-04-05T15:30:00+00:00", "2025-04-06T02:00:00+10:30", 0)]
    [InlineData("Asia/Kolkata", "2025-10-26T01:00:00+00:00", "2025-10-26T06:30:00+05:30", 0)]
    [InlineData("UTC", "2025-10-26T01:00:00+00:00", "2025-10-26T01:00:00+00:00", 0)]
    public void UtcInstantsInTheRepeatedHourCarryFoldOne(string zone, string utc, string local, int fold)
    {
        var moment = PythonDateTime.FromIsoFormat(utc).AsTimeZone(PythonZoneInfo.Create(zone));

        moment.IsoFormat().Should().Be(local);
        moment.Fold.Should().Be(fold);
    }

    [Theory]
    // A daily 02:15 run in Chicago skips 2025-03-09: 02:15 does not exist that day and resolves past the window's end.
    [InlineData("America/Chicago", "2025-03-07T08:15:00+00:00", "1d", "02:15", "02:45", "2025-03-07T08:15:00+00:00 2025-03-08T08:15:00+00:00 2025-03-10T07:15:00+00:00 2025-03-11T07:15:00+00:00 2025-03-12T07:15:00+00:00")]
    [InlineData("America/Chicago", "2025-10-31T06:30:00+00:00", "1d", "01:30", "01:45", "2025-10-31T06:30:00+00:00 2025-11-01T06:30:00+00:00 2025-11-02T06:30:00+00:00 2025-11-03T07:30:00+00:00 2025-11-04T07:30:00+00:00")]
    [InlineData("Europe/London", "2025-03-28T01:30:00+00:00", "1d", "01:30", "02:00", "2025-03-28T01:30:00+00:00 2025-03-29T01:30:00+00:00 2025-03-31T00:30:00+00:00 2025-04-01T00:30:00+00:00 2025-04-02T00:30:00+00:00")]
    [InlineData("Australia/Lord_Howe", "2025-10-02T15:15:00+00:00", "1d", "02:15", "02:20", "2025-10-02T15:45:00+00:00 2025-10-03T15:45:00+00:00 2025-10-05T15:15:00+00:00 2025-10-06T15:15:00+00:00 2025-10-07T15:15:00+00:00")]
    [InlineData("Australia/Lord_Howe", "2025-04-04T14:45:00+00:00", "12h", "01:45", "01:50", "2025-04-04T14:45:00+00:00 2025-04-05T14:45:00+00:00 2025-04-06T15:15:00+00:00 2025-04-07T15:15:00+00:00 2025-04-08T15:15:00+00:00")]
    [InlineData("Asia/Kolkata", "2025-03-07T20:30:00+00:00", "1d", "02:00", "02:10", "2025-03-07T20:30:00+00:00 2025-03-08T20:30:00+00:00 2025-03-09T20:30:00+00:00 2025-03-10T20:30:00+00:00 2025-03-11T20:30:00+00:00")]
    [InlineData("UTC", "2025-03-07T02:00:00+00:00", "1d", "02:00", "02:10", "2025-03-07T02:00:00+00:00 2025-03-08T02:00:00+00:00 2025-03-09T02:00:00+00:00 2025-03-10T02:00:00+00:00 2025-03-11T02:00:00+00:00")]
    public void WindowedChainsCrossTransitionsAsPythonDoes(string zone, string startAt, string every, string start, string end, string runs)
    {
        var spec = ScheduleSpec.FromDict(SchedulerParityTests.Payload(
            ("name", "n"),
            ("profile", "p"),
            ("every", every),
            ("start_at", startAt),
            ("window", SchedulerParityTests.Payload(("start", start), ("end", end), ("timezone", zone)))));

        var moment = spec.InitialRun();
        var actual = new List<string> { moment.IsoFormat() };
        for (var step = 0; step < 4; step++)
        {
            moment = spec.NextAfter(moment);
            actual.Add(moment.IsoFormat());
        }

        string.Join(' ', actual).Should().Be(runs);
    }
}
