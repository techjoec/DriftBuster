using DriftBuster.Backend.Models;
using DriftBuster.Backend.Scheduling;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary><c>schedules.json</c> and <c>scheduler-state.json</c>: strict reads, validated writes, and the schedule operations over them.</summary>
public sealed class ScheduleStoreTests : IDisposable
{
    private static readonly DateTimeOffset Jan1 = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-schedules-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Manifest => ScheduleStore.DefaultConfigPath(_tmp.FullName, overridePath: null);

    private string State => ScheduleStore.DefaultStatePath(_tmp.FullName, overridePath: null);

    private static ScheduleDefinition Nightly => new()
    {
        Name = "nightly",
        Profile = "nightly",
        Every = "24h",
        StartAt = "2025-01-01T00:00:00Z",
        Tags = ["ops"],
        Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["notes"] = "Rotation" },
        Window = new ScheduleWindowDefinition { Start = "00:00", End = "06:00", Timezone = "Europe/London" },
    };

    [Fact]
    public void Saved_schedules_read_back_as_written()
    {
        ScheduleStore.SaveSchedules([Nightly, Nightly with { Name = "hourly", Every = "1h", Window = null }], _tmp.FullName);

        var schedules = ScheduleStore.ListSchedules(_tmp.FullName).Schedules;
        schedules.Select(schedule => schedule.Name).Should().Equal("nightly", "hourly");
        schedules[0].Should().BeEquivalentTo(Nightly);
        File.ReadAllText(Manifest).Should().Contain("\"start_at\": \"2025-01-01T00:00:00Z\"").And.EndWith("}\n");
        Directory.GetFiles(Path.GetDirectoryName(Manifest)!, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void A_missing_manifest_holds_no_schedules()
        => ScheduleStore.ListSchedules(_tmp.FullName).Schedules.Should().BeEmpty();

    [Theory]
    [InlineData("""{"schedules": [{"name": "n", "profile": "p", "every": "1h", "colour": "red"}]}""", "$.schedules[0].colour")]
    [InlineData("""{"schedules": [{"name": "n", "name": "m", "profile": "p", "every": "1h"}]}""", "$.schedules[0].name")]
    [InlineData("""{"schedules": [{"name": "n", "profile": "p"}]}""", "every")]
    [InlineData("""{"schedules": [{"name": "n", "profile": "p", "every": 3600}]}""", "$.schedules[0].every")]
    [InlineData("""{"schedules": [{"name": "n", "profile": "p", "every": "1h", "metadata": {"n": 2}}]}""", "$.schedules[0].metadata.n")]
    [InlineData("""[{"name": "n", "profile": "p", "every": "1h"}]""", "$")]
    public void A_manifest_the_model_does_not_describe_is_refused_with_its_path(string json, string where)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Manifest)!);
        File.WriteAllText(Manifest, json);

        FluentActions.Invoking(() => ScheduleStore.ListSchedules(_tmp.FullName))
            .Should().Throw<ScheduleException>().Where(exc => exc.Message.StartsWith(Manifest + ": ", StringComparison.Ordinal) && exc.Message.Contains(where, StringComparison.Ordinal));
    }

    [Fact]
    public void Saving_refuses_invalid_or_repeated_schedules_by_index()
    {
        FluentActions.Invoking(() => ScheduleStore.SaveSchedules([Nightly, Nightly with { Every = "often" }], _tmp.FullName))
            .Should().Throw<ScheduleException>().WithMessage("schedules[1].every: Unsupported interval: 'often'.");
        FluentActions.Invoking(() => ScheduleStore.SaveSchedules([Nightly, Nightly], _tmp.FullName))
            .Should().Throw<ScheduleException>().WithMessage("schedules[1].name: 'nightly' is used by an earlier schedule.");
        File.Exists(Manifest).Should().BeFalse();

        ScheduleStore.ValidationError(Nightly).Should().BeNull();
        ScheduleStore.ValidationError(Nightly with { Profile = "" }).Should().Be("profile: required.");
    }

    [Fact]
    public void Commands_list_hand_out_complete_and_skip_runs()
    {
        ScheduleStore.SaveSchedules([Nightly], _tmp.FullName);
        var commands = new ScheduleCommands(_tmp.FullName, time: new FixedTimeProvider(Jan1.AddDays(1)));

        var run = commands.Due().Runs.Should().ContainSingle().Subject;
        run.Should().BeEquivalentTo(new ScheduleDueRun("nightly", "nightly", Jan1, ["ops"], Nightly.Metadata));
        ScheduleStore.LoadState(State)["nightly"].Should().Be(new ScheduleStateEntry(Jan1, Jan1));

        var status = commands.List().Schedules.Should().ContainSingle().Subject;
        status.IntervalSeconds.Should().Be(86400);
        status.Pending.Should().Be(Jan1);
        status.Window.Should().Be(new ScheduleWindowDefinition { Start = "00:00", End = "06:00", Timezone = "Europe/London" });

        commands.MarkComplete("nightly").Should().Be(new ScheduleStateResult("nightly", Jan1.AddDays(1), null));
        FluentActions.Invoking(() => commands.MarkComplete("nightly")).Should().Throw<ScheduleException>().WithMessage("Schedule 'nightly' is not pending.");

        var customState = Path.Join(_tmp.FullName, "state", "custom.json");
        new ScheduleCommands(_tmp.FullName, statePath: customState).SkipUntil("nightly", new DateTimeOffset(2025, 7, 1, 12, 0, 0, TimeSpan.Zero))
            .NextRun.Should().Be(new DateTimeOffset(2025, 7, 1, 23, 0, 0, TimeSpan.Zero));
        ScheduleStore.LoadState(customState)["nightly"].NextRun.Should().Be(new DateTimeOffset(2025, 7, 1, 23, 0, 0, TimeSpan.Zero));
        ScheduleStore.LoadState(State)["nightly"].NextRun.Should().Be(Jan1.AddDays(1));
    }

    [Fact]
    public void A_state_file_the_model_does_not_describe_is_refused()
    {
        ScheduleStore.SaveSchedules([Nightly], _tmp.FullName);
        File.WriteAllText(State, """{"nightly": {"next_run": "tomorrow", "pending": null}}""");

        FluentActions.Invoking(() => new ScheduleCommands(_tmp.FullName).List())
            .Should().Throw<ScheduleException>().Where(exc => exc.Message.StartsWith(State + ": ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_backend_facade_runs_the_same_operations()
    {
        var ct = TestContext.Current.CancellationToken;
        ScheduleStore.SaveSchedules([Nightly], _tmp.FullName);
        IDriftbusterBackend backend = new DriftbusterBackend();

        (await backend.ListSchedulesAsync(_tmp.FullName, ct)).Schedules.Should().ContainSingle();
        (await backend.ListDueSchedulesAsync(Jan1.AddDays(1), _tmp.FullName, configPath: " ", cancellationToken: ct)).Runs.Should().ContainSingle();
        (await backend.ListScheduleStatusAsync(_tmp.FullName, cancellationToken: ct)).Schedules[0].Pending.Should().Be(Jan1);
        (await backend.CompleteScheduleAsync("nightly", Jan1, _tmp.FullName, cancellationToken: ct)).NextRun.Should().Be(Jan1.AddDays(1));
        (await backend.SkipScheduleAsync("nightly", Jan1.AddDays(3), _tmp.FullName, cancellationToken: ct)).NextRun.Should().Be(Jan1.AddDays(3));
        await backend.SaveSchedulesAsync([Nightly with { Name = "other" }], _tmp.FullName, ct);
        (await backend.ListSchedulesAsync(_tmp.FullName, ct)).Schedules.Single().Name.Should().Be("other");
    }
}
