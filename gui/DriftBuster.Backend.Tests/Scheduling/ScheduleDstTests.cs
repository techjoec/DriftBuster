using System.Globalization;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Scheduling;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>
/// Windows and schedule chains across daylight saving changes in America/Chicago (the 2025-03-09 spring-forward gap
/// and the 2025-11-02 fall-back fold), Europe/London, Australia/Lord_Howe (30-minute DST), Asia/Kolkata (no DST) and UTC.
/// </summary>
public sealed class ScheduleDstTests
{
    [Theory]
    // A wall time in the gap takes the offset before it, so 02:00 CST is 03:00 CDT and 02:15 lands past a 02:30 end.
    [InlineData("America/Chicago", "02:00", "02:30", "2025-03-09T07:30:00+00:00", "2025-03-09T08:00:00+00:00", false)]
    [InlineData("America/Chicago", "02:00", "02:30", "2025-03-09T08:15:00+00:00", "2025-03-10T07:00:00+00:00", false)]
    // Inside the repeated hour: the second 01:10 is in a 01:00-01:59 window; moving to a repeated start takes the occurrence not before
    // the candidate, so 01:30 is the CST one.
    [InlineData("America/Chicago", "01:00", "01:59", "2025-11-02T07:10:00+00:00", "2025-11-02T07:10:00+00:00", true)]
    [InlineData("America/Chicago", "01:00", "01:59", "2025-11-02T08:10:00+00:00", "2025-11-03T07:00:00+00:00", false)]
    [InlineData("America/Chicago", "01:30", "03:00", "2025-11-02T07:00:00+00:00", "2025-11-02T07:30:00+00:00", false)]
    [InlineData("Europe/London", "01:15", "01:45", "2025-03-30T00:30:00+00:00", "2025-03-30T01:15:00+00:00", false)]
    [InlineData("Europe/London", "01:15", "01:45", "2025-10-26T00:50:00+00:00", "2025-10-27T01:15:00+00:00", false)]
    [InlineData("Australia/Lord_Howe", "01:45", "02:15", "2025-04-05T14:40:00+00:00", "2025-04-05T14:45:00+00:00", false)]
    [InlineData("Australia/Lord_Howe", "02:00", "02:30", "2025-10-04T15:00:00+00:00", "2025-10-04T15:30:00+00:00", false)]
    [InlineData("Asia/Kolkata", "22:00", "05:30", "2025-03-09T23:59:00+00:00", "2025-03-09T23:59:00+00:00", true)]
    [InlineData("UTC", "09:00", "17:00", "2025-03-09T17:00:00.999999+00:00", "2025-03-09T17:00:00+00:00", false)]
    public void WindowAlignsAndContainsAcrossTransitions(string zone, string start, string end, string candidate, string aligned, bool contains)
    {
        var window = ScheduleWindow.FromDict(SchedulerTests.Payload(("start", start), ("end", end), ("timezone", zone)));
        var moment = DateTimeOffset.Parse(candidate, CultureInfo.InvariantCulture);

        IsoTimestamp.Format(window.Align(moment)).Should().Be(aligned);
        window.Contains(moment).Should().Be(contains);
    }

    [Theory]
    // A daily 02:15 run in Chicago skips 2025-03-09: 02:15 does not exist that day and resolves past the window's end.
    [InlineData("America/Chicago", "2025-03-07T08:15:00+00:00", "1d", "02:15", "02:45", "2025-03-07T08:15:00+00:00 2025-03-08T08:15:00+00:00 2025-03-10T07:15:00+00:00 2025-03-11T07:15:00+00:00 2025-03-12T07:15:00+00:00")]
    [InlineData("America/Chicago", "2025-10-31T06:30:00+00:00", "1d", "01:30", "01:45", "2025-10-31T06:30:00+00:00 2025-11-01T06:30:00+00:00 2025-11-02T06:30:00+00:00 2025-11-03T07:30:00+00:00 2025-11-04T07:30:00+00:00")]
    [InlineData("Europe/London", "2025-03-28T01:30:00+00:00", "1d", "01:30", "02:00", "2025-03-28T01:30:00+00:00 2025-03-29T01:30:00+00:00 2025-03-31T00:30:00+00:00 2025-04-01T00:30:00+00:00 2025-04-02T00:30:00+00:00")]
    [InlineData("Australia/Lord_Howe", "2025-10-02T15:15:00+00:00", "1d", "02:15", "02:20", "2025-10-02T15:45:00+00:00 2025-10-03T15:45:00+00:00 2025-10-05T15:15:00+00:00 2025-10-06T15:15:00+00:00 2025-10-07T15:15:00+00:00")]
    [InlineData("UTC", "2025-03-07T02:00:00+00:00", "1d", "02:00", "02:10", "2025-03-07T02:00:00+00:00 2025-03-08T02:00:00+00:00 2025-03-09T02:00:00+00:00 2025-03-10T02:00:00+00:00 2025-03-11T02:00:00+00:00")]
    public void WindowedChainsCrossTransitions(string zone, string startAt, string every, string start, string end, string runs)
    {
        var spec = ScheduleSpec.FromDict(SchedulerTests.Payload(
            ("name", "n"),
            ("profile", "p"),
            ("every", every),
            ("start_at", startAt),
            ("window", SchedulerTests.Payload(("start", start), ("end", end), ("timezone", zone)))));

        var moment = spec.InitialRun();
        var actual = new List<string> { IsoTimestamp.Format(moment) };
        for (var step = 0; step < 4; step++)
        {
            moment = spec.NextAfter(moment);
            actual.Add(IsoTimestamp.Format(moment));
        }

        string.Join(' ', actual).Should().Be(runs);
    }
}
