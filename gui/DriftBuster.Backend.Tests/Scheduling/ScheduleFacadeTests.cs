using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>
/// The schedule operations of <see cref="DriftbusterBackend"/> the PowerShell cmdlets call: list with state, due, complete and skip, over a
/// manifest in a temporary base directory, with custom manifest and state paths and blank paths meaning the defaults.
/// </summary>
public sealed class ScheduleFacadeTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-schedule-facade-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public async Task FacadeRunsTheScheduleCommands()
    {
        var baseDir = _tmp.FullName;
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(baseDir, "Profiles"));
        await File.WriteAllTextAsync(
            Path.Combine(baseDir, "Profiles", "schedules.json"),
            """{"schedules": [{"name": "nightly", "profile": "nightly", "every": "24h", "start_at": "2025-01-01T00:00:00Z", "tags": "ops", "metadata": {"notes": "Rotation", "n": 2}, "window": {"start": "00:00", "end": "06:00", "timezone": "Europe/London"}}]}""",
            new UTF8Encoding(false),
            ct);
        IDriftbusterBackend backend = new DriftbusterBackend();

        var due = await backend.ListDueSchedulesAsync("2025-01-02T00:00:00Z", baseDir, configPath: " ", statePath: null, ct);
        var run = due.Runs.Should().ContainSingle().Subject;
        run.Name.Should().Be("nightly");
        run.Profile.Should().Be("nightly");
        run.ScheduledFor.Should().Be("2025-01-01T00:00:00+00:00");
        run.Tags.Should().Equal("ops");
        run.Metadata["notes"].Should().Be("Rotation");
        run.Metadata["n"].Should().Be(2);

        var listing = await backend.ListScheduleStatusAsync(baseDir, cancellationToken: ct);
        var status = listing.Schedules.Should().ContainSingle().Subject;
        status.IntervalSeconds.Should().Be(86400.0);
        status.StartAt.Should().Be("2025-01-01T00:00:00+00:00");
        status.Pending.Should().Be("2025-01-01T00:00:00+00:00");
        status.Window!.Timezone.Should().Be("Europe/London");
        status.Window.Start.Should().Be("00:00:00");

        var completed = await backend.CompleteScheduleAsync("nightly", completedAt: null, baseDir, cancellationToken: ct);
        completed.Name.Should().Be("nightly");
        completed.NextRun.Should().Be("2025-01-02T00:00:00+00:00");
        completed.Pending.Should().BeNull();

        var customState = Path.Combine(baseDir, "state", "custom.json");
        var skipped = await backend.SkipScheduleAsync("nightly", "2025-07-01T12:00:00+00:00", baseDir, statePath: customState, cancellationToken: ct);
        skipped.NextRun.Should().Be("2025-07-01T23:00:00+00:00");
        File.Exists(customState).Should().BeTrue();

        (await backend.ListScheduleStatusAsync(baseDir, statePath: customState, cancellationToken: ct)).Schedules[0].NextRun.Should().Be("2025-07-01T23:00:00+00:00");
        (await backend.ListScheduleStatusAsync(baseDir, cancellationToken: ct)).Schedules[0].NextRun.Should().Be("2025-01-02T00:00:00+00:00");

        await FluentActions.Awaiting(() => backend.CompleteScheduleAsync("nightly", null, baseDir, cancellationToken: ct))
            .Should().ThrowAsync<CommandExitException>().WithMessage("Schedule nightly is not pending.");
    }
}
