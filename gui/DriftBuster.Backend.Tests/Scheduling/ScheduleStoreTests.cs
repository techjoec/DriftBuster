using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Scheduling;

using static DriftBuster.Backend.Tests.Scheduling.SchedulerTests;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>
/// <see cref="ScheduleStore"/> and the edges of <see cref="ProfileScheduler"/> and <see cref="ScheduleCommands"/>, including the GUI
/// manifest writer and reader.
/// </summary>
public sealed class ScheduleStoreTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-schedule-store-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_tmp.FullName, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Utf8);
        return path;
    }

    private static ScheduleSpec Spec(string name = "a")
        => ScheduleSpec.FromDict(Payload(("name", name), ("profile", "p"), ("every", "1h"), ("start_at", "2025-01-01T00:00:00Z")));

    [Theory]
    [InlineData("""{"schedules": "abc"}""", "[]")]
    [InlineData("""[1, {"name": "x"}]""", "[{'name': 'x'}]")]
    [InlineData("null", "[]")]
    [InlineData("""{"schedules": [{"name": "a"}]}""", "[{'name': 'a'}]")]
    public void LoadSchedulePayloadKeepsMappingEntries(string text, string expected)
    {
        var entries = ScheduleStore.LoadSchedulePayload(Write("m.json", text));
        EngineRepr.Repr(entries.Cast<object?>().ToList()).Should().Be(expected);
    }

    [Fact]
    public void LoadSchedulePayloadReportsMissingAndInvalidFiles()
    {
        var missing = Path.Combine(_tmp.FullName, "missing.json");
        FluentActions.Invoking(() => ScheduleStore.LoadSchedulePayload(missing))
            .Should().Throw<CommandExitException>().WithMessage($"Schedule manifest not found: {missing}");
        var invalid = Write("bad.json", "{bad");
        FluentActions.Invoking(() => ScheduleStore.LoadSchedulePayload(invalid))
            .Should().Throw<CommandExitException>().WithMessage($"Failed to parse schedules from {invalid}: invalid JSON document");
    }

    [Fact]
    public void LoadScheduleStateNormalisesEntriesAndRefusesOtherPayloads()
    {
        ScheduleStore.LoadScheduleState(Path.Combine(_tmp.FullName, "none.json")).Should().BeEmpty();
        var state = ScheduleStore.LoadScheduleState(Write("s.json", """{"a": 5, "b": {"next_run": "2025-01-01", "pending": null, "x": 1}}"""));
        EngineRepr.Repr(state).Should().Be("{'b': {'next_run': '2025-01-01', 'pending': None}}");
        FluentActions.Invoking(() => ScheduleStore.LoadScheduleState(Write("list.json", "[1]")))
            .Should().Throw<CommandExitException>().WithMessage("Scheduler state payload must be a JSON object.");
        var invalid = Write("bad.json", "{bad");
        FluentActions.Invoking(() => ScheduleStore.LoadScheduleState(invalid))
            .Should().Throw<CommandExitException>().WithMessage($"Failed to parse scheduler state from {invalid}: invalid JSON document");
    }

    [Fact]
    public void ApplyStateConvertsTimestampsToUtcAndRaisesOnBadValues()
    {
        var scheduler = new ProfileScheduler([Spec()]);
        FluentActions.Invoking(() => scheduler.ApplyState(Payload(("a", Payload(("next_run", "garbage"), ("pending", null))))))
            .Should().Throw<ScheduleException>().WithMessage("Unable to parse timestamp: 'garbage'");
        FluentActions.Invoking(() => scheduler.ApplyState(Payload(("a", Payload(("next_run", 5), ("pending", null))))))
            .Should().Throw<ScheduleException>().WithMessage("Unable to parse timestamp: 5");
        FluentActions.Invoking(() => scheduler.ApplyState(Payload(("a", Payload(("next_run", "0001-01-01T00:00:00+01:00"))))))
            .Should().Throw<ScheduleException>().WithMessage("Unable to parse timestamp: '0001-01-01T00:00:00+01:00'");

        scheduler.ApplyState(Payload(("a", Payload(("next_run", "2025-01-01T05:00:00+05:30"), ("pending", "2025-01-01T00:00:00.5"))), ("unknown", 7)));
        EngineRepr.Repr(scheduler.SnapshotState())
            .Should().Be("{'a': {'next_run': '2024-12-31T23:30:00+00:00', 'pending': '2025-01-01T00:00:00.500000+00:00'}}");

        var path = Path.Combine(_tmp.FullName, "deep", "st.json");
        ScheduleStore.WriteScheduleState(scheduler, path);
        File.ReadAllText(path, Utf8).ReplaceLineEndings("\n")
            .Should().Be("{\n  \"a\": {\n    \"next_run\": \"2024-12-31T23:30:00+00:00\",\n    \"pending\": \"2025-01-01T00:00:00.500000+00:00\"\n  }\n}\n");
    }

    [Fact]
    public void SchedulerOperationsRaiseScheduleErrors()
    {
        var spec = Spec();
        var scheduler = new ProfileScheduler([spec]);
        FluentActions.Invoking(() => scheduler.MarkComplete("a")).Should().Throw<ScheduleException>().WithMessage("Schedule a is not pending.");
        FluentActions.Invoking(() => scheduler.MarkComplete("zz")).Should().Throw<ScheduleException>().WithMessage("Unknown schedule: zz");
        FluentActions.Invoking(() => scheduler.SkipUntil("zz", IsoTimestamp.UtcNow())).Should().Throw<ScheduleException>().WithMessage("Unknown schedule: zz");
        FluentActions.Invoking(() => scheduler.Peek("zz")).Should().Throw<ScheduleException>().WithMessage("Unknown schedule: zz");
        FluentActions.Invoking(() => scheduler.Cancel("zz")).Should().Throw<ScheduleException>().WithMessage("Unknown schedule: zz");
        FluentActions.Invoking(() => scheduler.Register(spec)).Should().Throw<ScheduleException>().WithMessage("Schedule already registered: a");
        FluentActions.Invoking(() => spec.LoadProfile()).Should().Throw<ScheduleException>().WithMessage("Profile loader not configured for this schedule");

        var run = scheduler.Due(new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero)).Single();
        FluentActions.Invoking(() => run.LoadProfile()).Should().Throw<ScheduleException>().WithMessage("A profile loader is required to hydrate the run.");
        run.LoadProfile(name => new DriftBuster.Backend.Profiles.Run.RunProfile(name)).Name.Should().Be("p");

        scheduler.Cancel("a");
        scheduler.SnapshotState().Should().BeEmpty();
        scheduler.Register(Spec("b"));
        scheduler.Register(Spec("a"));
        scheduler.Schedules().Select(item => item.Name).Should().Equal("a", "b");
        scheduler.SnapshotState().Keys.Should().Equal("b", "a");
        FluentActions.Invoking(() => ScheduleCommands.ParseReferenceTimestamp("nope"))
            .Should().Throw<CommandExitException>().WithMessage("Unable to parse timestamp: 'nope'");
    }

    [Fact]
    public void CommandsReportManifestAndSchedulerErrorsAsExits()
    {
        var baseDir = _tmp.FullName;
        Write(Path.Combine("Profiles", "schedules.json"), """[{"name": "a", "profile": "p", "every": "nope"}]""");
        FluentActions.Invoking(() => ScheduleCommands.List(baseDir))
            .Should().Throw<CommandExitException>().WithMessage("Unsupported interval fragment near: nope");

        Write(Path.Combine("Profiles", "schedules.json"), """[{"name": "a", "profile": "p", "every": "1h", "start_at": "2025-01-01T00:00:00Z", "window": {"start": "01:00", "end": "02:00", "timezone": "Asia/Kolkata"}, "tags": ["z", "b"], "metadata": {"n": [1, 2.5]}}]""");
        FluentActions.Invoking(() => ScheduleCommands.MarkComplete("a", null, baseDir))
            .Should().Throw<CommandExitException>().WithMessage("Schedule a is not pending.");
        FluentActions.Invoking(() => ScheduleCommands.SkipUntil("zz", "2025-01-01", baseDir))
            .Should().Throw<CommandExitException>().WithMessage("Unknown schedule: zz");

        var listing = ScheduleCommands.List(baseDir);
        EngineRepr.Repr(listing.ToList()).Should().Be(
            "[{'name': 'a', 'profile': 'p', 'interval_seconds': 3600.0, 'tags': ['b', 'z'], 'metadata': {'n': [1, 2.5]}, "
            + "'start_at': '2025-01-01T00:00:00+00:00', 'next_run': '2025-01-01T19:30:00+00:00', 'pending': None, "
            + "'window': {'start': '01:00:00', 'end': '02:00:00', 'timezone': 'Asia/Kolkata'}}]");
        var custom = Path.Combine(_tmp.FullName, "elsewhere", "state.json");
        EngineRepr.Repr(ScheduleCommands.Due("2025-01-02T00:00:00", baseDir, statePath: custom).ToList())
            .Should().Be("[{'name': 'a', 'profile': 'p', 'scheduled_for': '2025-01-01T19:30:00+00:00', 'tags': ['b', 'z'], 'metadata': {'n': [1, 2.5]}}]");
        File.Exists(custom).Should().BeTrue();
        EngineRepr.Repr(ScheduleCommands.MarkComplete("a", null, baseDir, statePath: custom))
            .Should().Be("{'name': 'a', 'next_run': '2025-01-01T20:30:00+00:00', 'pending': None}");
    }

    [Fact]
    public void GuiManifestRoundTripsCardsAsJsonDumpsWritesThem()
    {
        var baseDir = _tmp.FullName;
        ScheduleStore.SaveSchedules(
            [
                new ScheduleDefinition
                {
                    Name = " nightly ",
                    Profile = " daily ",
                    Every = " 24h ",
                    StartAt = " 2025-01-01T02:00:00+00:00 ",
                    Window = new ScheduleWindowDefinition { Start = " 08:00 ", End = "17:00", Timezone = "UTC" },
                    Tags = ["prod", "Prod", " staging "],
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [" team "] = "infra", [" "] = "dropped" },
                },
                new ScheduleDefinition { Name = "alpha", Profile = "p", Every = "90s" },
            ],
            baseDir,
            TestContext.Current.CancellationToken);

        var text = File.ReadAllText(Path.Combine(baseDir, "Profiles", "schedules.json")).ReplaceLineEndings("\n");
        // Two-space indented JSON with a trailing newline.
        text.Should().Be(
            "{\n  \"schedules\": [\n    {\n      \"name\": \"nightly\",\n      \"profile\": \"daily\",\n      \"every\": \"24h\",\n"
            + "      \"start_at\": \"2025-01-01T02:00:00+00:00\",\n      \"window\": {\n        \"start\": \"08:00\",\n"
            + "        \"end\": \"17:00\",\n        \"timezone\": \"UTC\"\n      },\n      \"tags\": [\n        \"prod\",\n        \"staging\"\n      ],\n"
            + "      \"metadata\": {\n        \"team\": \"infra\"\n      }\n    },\n    {\n      \"name\": \"alpha\",\n      \"profile\": \"p\",\n"
            + "      \"every\": \"90s\"\n    }\n  ]\n}\n");

        var loaded = ScheduleStore.ListSchedules(baseDir, TestContext.Current.CancellationToken).Schedules;
        loaded.Select(schedule => schedule.Name).Should().Equal("nightly", "alpha");
        loaded[0].StartAt.Should().Be("2025-01-01T02:00:00+00:00");
        loaded[0].Window!.Start.Should().Be("08:00");
        loaded[0].Tags.Should().Equal("prod", "staging");
        loaded[0].Metadata.Should().ContainKey("team");
    }

    [Fact]
    public void GuiManifestReaderSkipsIncompleteEntries()
    {
        var baseDir = _tmp.FullName;
        ScheduleStore.ListSchedules(baseDir, TestContext.Current.CancellationToken).Schedules.Should().BeEmpty();
        Write(Path.Combine("Profiles", "schedules.json"), "\"text\"");
        ScheduleStore.ListSchedules(baseDir, TestContext.Current.CancellationToken).Schedules.Should().BeEmpty();
        Write(
            Path.Combine("Profiles", "schedules.json"),
            """[{"name": "b", "profile": "p", "every": "1h", "tags": "one", "metadata": {"k": null}, "window": {}}, {"name": " ", "profile": "p", "every": "1h"}, {"profile": "p"}, 3]""");
        var loaded = ScheduleStore.ListSchedules(baseDir, TestContext.Current.CancellationToken).Schedules.Should().ContainSingle().Subject;
        loaded.Tags.Should().Equal("one");
        loaded.Metadata["k"].Should().BeEmpty();
        loaded.Window.Should().BeNull();
    }

    [Theory]
    [InlineData("bad", null, null, null, null, "Unsupported interval fragment near: bad")]
    [InlineData("0s", null, null, null, null, "Interval must be positive.")]
    [InlineData("1h", "02:00", null, null, null, "Invalid ISO 8601 timestamp: '02:00'")]
    [InlineData("1h", null, "08:00", null, "UTC", "Window requires start and end fields")]
    [InlineData("1h", null, "08:00", "17:00", "Mars/Olympus", "Unknown time zone: Mars/Olympus")]
    [InlineData("1h", "2025-01-01T00:00:00Z", "08:00", "17:00", "America/Chicago", null)]
    public void GuiCardsValidateThroughTheSpecRules(string every, string? startAt, string? start, string? end, string? timezone, string? error)
    {
        var definition = new ScheduleDefinition
        {
            Name = "card",
            Profile = "p",
            Every = every,
            StartAt = startAt,
            Window = start is null && end is null && timezone is null ? null : new ScheduleWindowDefinition { Start = start, End = end, Timezone = timezone },
        };

        ScheduleStore.ValidationError(definition).Should().Be(error);
        var save = () => ScheduleStore.SaveSchedules([definition], _tmp.FullName);
        if (error is null)
        {
            save.Should().NotThrow();
        }
        else
        {
            save.Should().Throw<Exception>().WithMessage(error);
        }
    }

    [Fact]
    public void GuiSaveRefusesMissingFieldsAndRepeatedNames()
    {
        FluentActions.Invoking(() => ScheduleStore.SaveSchedules([new ScheduleDefinition { Profile = "p", Every = "1h" }], _tmp.FullName))
            .Should().Throw<InvalidOperationException>().WithMessage("Schedule name is required.");
        FluentActions.Invoking(() => ScheduleStore.SaveSchedules([new ScheduleDefinition { Name = "n", Every = "1h" }], _tmp.FullName))
            .Should().Throw<InvalidOperationException>().WithMessage("Schedule 'n' is missing a profile reference.");
        FluentActions.Invoking(() => ScheduleStore.SaveSchedules([new ScheduleDefinition { Name = "n", Profile = "p" }], _tmp.FullName))
            .Should().Throw<InvalidOperationException>().WithMessage("Schedule 'n' is missing an interval.");
        ScheduleStore.ValidationError(new ScheduleDefinition { Name = "n", Profile = "p" }).Should().Be("Schedule 'n' is missing an interval.");
        FluentActions.Invoking(() => ScheduleStore.SaveSchedules(
                [null!, new ScheduleDefinition { Name = "n", Profile = "p", Every = "1h" }, new ScheduleDefinition { Name = " n ", Profile = "q", Every = "2h" }],
                _tmp.FullName))
            .Should().Throw<ScheduleException>().WithMessage("Schedule already registered: n");
    }
}
