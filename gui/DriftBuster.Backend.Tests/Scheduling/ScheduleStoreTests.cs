using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Scheduling;

using static DriftBuster.Backend.Tests.Scheduling.SchedulerParityTests;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>
/// <see cref="ScheduleStore"/> and the edges of <see cref="ProfileScheduler"/> and <see cref="ScheduleCommands"/>. Messages and payloads are
/// CPython 3.13's for the same files and calls, except where noted: a JSON decode failure carries no decoder position (as documented for
/// <c>PythonJson</c>). The GUI manifest tests pin the writer and reader the facade has always used.
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
    [InlineData("""{"schedules": 0}""", "[]")]
    [InlineData("""[1, {"name": "x"}]""", "[{'name': 'x'}]")]
    [InlineData("""{"other": 1}""", "[]")]
    [InlineData("null", "[]")]
    [InlineData("\"text\"", "[]")]
    [InlineData("""{"schedules": [{"name": "a"}]}""", "[{'name': 'a'}]")]
    public void LoadSchedulePayloadKeepsMappingEntries(string text, string expected)
    {
        var entries = ScheduleStore.LoadSchedulePayload(Write("m.json", text));
        PythonRepr.Repr(entries.Cast<object?>().ToList()).Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"schedules": {"a": 1}}""")]
    [InlineData("5")]
    [InlineData("true")]
    public void LoadSchedulePayloadRefusesPayloadsThatAreNotArrays(string text)
        => FluentActions.Invoking(() => ScheduleStore.LoadSchedulePayload(Write("m.json", text)))
            .Should().Throw<CommandExitException>().WithMessage("Schedules payload must be an array of schedule entries.");

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

    // CPython 3.13: _load_schedule_payload and _load_schedule_state catch only JSONDecodeError, so json.loads' interpreter limits escape.
    [Fact]
    public void TheLoadersLetTheDecoderLimitsEscape()
    {
        var bigInt = Write("big.json", "{\"n\": " + new string('7', 4301) + "}");
        FluentActions.Invoking(() => ScheduleStore.LoadSchedulePayload(bigInt)).Should().Throw<PythonValueException>().WithMessage("Exceeds the limit (4300 digits)*");
        FluentActions.Invoking(() => ScheduleStore.LoadScheduleState(bigInt)).Should().Throw<PythonValueException>().WithMessage("*value has 4301 digits*");
        var deep = Write("deep.json", new string('[', 9999) + new string(']', 9999));
        FluentActions.Invoking(() => ScheduleStore.LoadSchedulePayload(deep))
            .Should().Throw<PythonRecursionException>().WithMessage("maximum recursion depth exceeded while decoding a JSON array from a unicode string");
    }

    [Fact]
    public void LoadScheduleStateNormalisesEntriesAndRefusesOtherPayloads()
    {
        ScheduleStore.LoadScheduleState(Path.Combine(_tmp.FullName, "none.json")).Should().BeEmpty();
        var state = ScheduleStore.LoadScheduleState(Write("s.json", """{"a": 5, "b": {"next_run": "2025-01-01", "pending": null, "x": 1}}"""));
        PythonRepr.Repr(state).Should().Be("{'b': {'next_run': '2025-01-01', 'pending': None}}");
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
            .Should().Throw<OverflowException>().WithMessage("date value out of range");

        scheduler.ApplyState(Payload(("a", Payload(("next_run", "2025-01-01T05:00:00+05:30"), ("pending", "2025-01-01T00:00:00.5"))), ("unknown", 7)));
        PythonRepr.Repr(scheduler.SnapshotState())
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
        FluentActions.Invoking(() => scheduler.SkipUntil("zz", PythonDateTime.UtcNow())).Should().Throw<ScheduleException>().WithMessage("Unknown schedule: zz");
        FluentActions.Invoking(() => scheduler.Peek("zz")).Should().Throw<ScheduleException>().WithMessage("Unknown schedule: zz");
        FluentActions.Invoking(() => scheduler.Cancel("zz")).Should().Throw<ScheduleException>().WithMessage("Unknown schedule: zz");
        FluentActions.Invoking(() => scheduler.Register(spec)).Should().Throw<ScheduleException>().WithMessage("Schedule already registered: a");
        FluentActions.Invoking(() => spec.LoadProfile()).Should().Throw<ScheduleException>().WithMessage("Profile loader not configured for this schedule");

        var run = scheduler.Due(PythonDateTime.Create(2025, 1, 2, tz: PythonFixedOffset.Utc)).Single();
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
        PythonRepr.Repr(listing.ToList()).Should().Be(
            "[{'name': 'a', 'profile': 'p', 'interval_seconds': 3600.0, 'tags': ['b', 'z'], 'metadata': {'n': [1, 2.5]}, "
            + "'start_at': '2025-01-01T00:00:00+00:00', 'next_run': '2025-01-01T19:30:00+00:00', 'pending': None, "
            + "'window': {'start': '01:00:00', 'end': '02:00:00', 'timezone': 'Asia/Kolkata'}}]");
        var custom = Path.Combine(_tmp.FullName, "elsewhere", "state.json");
        PythonRepr.Repr(ScheduleCommands.Due("2025-01-02T00:00:00", baseDir, statePath: custom).ToList())
            .Should().Be("[{'name': 'a', 'profile': 'p', 'scheduled_for': '2025-01-01T19:30:00+00:00', 'tags': ['b', 'z'], 'metadata': {'n': [1, 2.5]}}]");
        File.Exists(custom).Should().BeTrue();
        PythonRepr.Repr(ScheduleCommands.MarkComplete("a", null, baseDir, statePath: custom))
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
        // CPython 3.13: json.dumps(payload, indent=2) + "\n" of the same payload.
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
    [InlineData("90", null, null, null, null, "Unsupported interval fragment near: 90")]
    [InlineData("0s", null, null, null, null, "Interval must be positive.")]
    [InlineData("1h", "02:00", null, null, null, "Invalid isoformat string: '02:00'")]
    [InlineData("1h", null, "08:00", null, "UTC", "Window requires start and end fields")]
    [InlineData("1h", null, "08:00", "25:00", "UTC", "hour must be in 0..23")]
    [InlineData("1h", null, "08", "17:00", "UTC", "Time must be HH:MM or HH:MM:SS")]
    [InlineData("1h", null, "08:00", "17:00", "Mars/Olympus", "Unknown time zone: Mars/Olympus")]
    [InlineData("PT", null, null, null, null, "Unsupported ISO-8601 interval: 'PT'")]
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
    // CPython 3.13: run_profiles_cli schedule list over each manifest gives start_at None (a falsy start_at is no start).
    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("[]")]
    public void GuiCardsReadAFalsyStartAtAsNoStart(string startAt)
    {
        var manifest = Write(Path.Combine("Profiles", "schedules.json"), $$"""{"schedules": [{"name": "a", "profile": "p", "every": "1h", "start_at": {{startAt}}}]}""");
        var card = ScheduleStore.ListSchedules(_tmp.FullName, TestContext.Current.CancellationToken).Schedules.Should().ContainSingle().Subject;

        card.StartAt.Should().BeNull();
        ScheduleStore.ValidationError(card).Should().BeNull();
        ScheduleStore.SaveSchedules([card], _tmp.FullName, TestContext.Current.CancellationToken);
        PythonJson.TryLoads(File.ReadAllText(manifest), out var saved).Should().BeTrue();
        PythonRepr.Repr(saved).Should().Be($"{{'schedules': [{{'name': 'a', 'profile': 'p', 'every': '1h', 'start_at': {PythonRepr.Repr(Decode(startAt))}}}]}}");
        ((IReadOnlyDictionary<string, object?>)ScheduleCommands.List(_tmp.FullName).Single()!)["start_at"].Should().BeNull();
    }

    // CPython 3.13: the manifest loads with from_dict, and ProfileScheduler(specs) raises OverflowError('date value out of range') when the
    // window aligns the first run past 9999-12-31.
    [Fact]
    public void GuiSaveRegistersEachCardAsTheSchedulerDoes()
    {
        var card = new ScheduleDefinition
        {
            Name = "a",
            Profile = "p",
            Every = "1h",
            StartAt = "9999-12-31T23:00:00Z",
            Window = new ScheduleWindowDefinition { Start = "01:00", End = "02:00" },
        };

        ScheduleStore.ValidationError(card).Should().Be("date value out of range");
        FluentActions.Invoking(() => ScheduleStore.SaveSchedules([card], _tmp.FullName, TestContext.Current.CancellationToken))
            .Should().Throw<OverflowException>().WithMessage("date value out of range");
        File.Exists(Path.Combine(_tmp.FullName, "Profiles", "schedules.json")).Should().BeFalse();
    }

    // CPython 3.13, run_profiles_cli schedule list over the manifest: names 'None', '100.0', 'b' and 'nightly ', tags "{'x': 1}" and
    // ['100.0', 'B', 'None', 'b'], metadata {'k': '1', ' k': '2'}. A load and an unchanged save must leave the listing as it was.
    [Fact]
    public void GuiLoadAndSaveLeavesWhatTheSchedulerReads()
    {
        const string start = "\"start_at\": \"2025-01-01T00:00:00Z\"";
        var manifest = Write(
            Path.Combine("Profiles", "schedules.json"),
            "{\"schedules\": ["
            + $"{{\"name\": null, \"profile\": \"p\", \"every\": \"1h\", {start}, \"extra\": [1]}},"
            + $"{{\"name\": \"b\", \"profile\": \"p\", \"every\": \"1h\", {start}, \"tags\": {{\"x\": 1}}}},"
            + $"{{\"name\": 1e2, \"profile\": \"p\", \"every\": 3600, {start}, \"tags\": [null, 1e2, \"B\", \"b\"]}},"
            + $"{{\"name\": \"nightly \", \"profile\": \" p\", \"every\": \"1h\", {start}, \"metadata\": {{\"k\": \"1\", \" k\": \"2\"}}, \"window\": {{\"start\": \"01:00\", \"end\": \"02:00\"}}}}"
            + "]}");
        var before = PythonRepr.Repr(ScheduleCommands.List(_tmp.FullName).ToList());
        before.Should().Contain("'name': 'None'").And.Contain("'name': '100.0'").And.Contain("'name': 'nightly '")
            .And.Contain("'tags': [\"{'x': 1}\"]").And.Contain("'tags': ['100.0', 'B', 'None', 'b']").And.Contain("'metadata': {'k': '1', ' k': '2'}");

        var cards = ScheduleStore.ListSchedules(_tmp.FullName, TestContext.Current.CancellationToken).Schedules;
        cards.Select(card => card.Name).Should().Equal("None", "b", "100.0", "nightly");
        cards[2].Tags.Should().Equal("None", "100.0", "B");
        cards.Should().AllSatisfy(card => ScheduleStore.ValidationError(card).Should().BeNull());
        ScheduleStore.SaveSchedules(cards, _tmp.FullName, TestContext.Current.CancellationToken);

        PythonRepr.Repr(ScheduleCommands.List(_tmp.FullName).ToList()).Should().Be(before);
        File.ReadAllText(manifest).Should().Contain("\"extra\": [");
    }

    // A manifest nested thousands of levels deep loads and saves unchanged without the indented layout's quadratic growth: containers 64
    // levels deep or more are written on one line, and json.loads reads the same values back.
    [Fact]
    public void GuiSaveWritesDeepNestingOnOneLine()
    {
        const int depth = 9990;
        var lists = string.Concat(Enumerable.Repeat("[", depth)) + string.Concat(Enumerable.Repeat("]", depth));
        var objects = string.Concat(Enumerable.Repeat("{\"k\": ", 9000)) + "1" + string.Concat(Enumerable.Repeat("}", 9000));
        var manifest = Write(
            Path.Combine("Profiles", "schedules.json"),
            $"{{\"schedules\": [{{\"name\": \"a\", \"profile\": \"p\", \"every\": \"1h\", \"metadata\": {{\"deep\": {lists}}}, \"extra\": {objects}}}]}}");
        var before = Canonicaliser.Dumps(ScheduleStore.LoadSchedulePayload(manifest).Cast<object?>().ToList(), indent: false, ensureAscii: true, sortKeys: false);
        var size = new FileInfo(manifest).Length;

        var cards = ScheduleStore.ListSchedules(_tmp.FullName, TestContext.Current.CancellationToken).Schedules;
        ScheduleStore.SaveSchedules(cards, _tmp.FullName, TestContext.Current.CancellationToken);

        new FileInfo(manifest).Length.Should().BeLessThan(size * 2);
        Canonicaliser.Dumps(ScheduleStore.LoadSchedulePayload(manifest).Cast<object?>().ToList(), indent: false, ensureAscii: true, sortKeys: false)
            .Should().Be(before);
        var nested = new List<object?> { new List<object?> { 1, new List<object?> { 2, 3 } } };
        Canonicaliser.Dumps(nested, indent: true, ensureAscii: false, sortKeys: false, maxIndentDepth: 1).Should().Be("[\n  [1, [2, 3]]\n]");
    }

    // CPython 3.13: ScheduleWindow.from_dict raises SystemExit('Unknown time zone: None') for a null time zone, which the card keeps.
    [Fact]
    public void GuiCardsKeepAWindowTheSchedulerRefuses()
    {
        Write(Path.Combine("Profiles", "schedules.json"), """[{"name": "a", "profile": "p", "every": "1h", "window": {"start": "01:00", "end": "02:00", "timezone": null}}]""");
        var card = ScheduleStore.ListSchedules(_tmp.FullName, TestContext.Current.CancellationToken).Schedules.Should().ContainSingle().Subject;

        card.Window!.Timezone.Should().Be("None");
        ScheduleStore.ValidationError(card).Should().Be("Unknown time zone: None");
        card.Window.Timezone = null;
        ScheduleStore.ValidationError(card).Should().BeNull("an edited window without a time zone is written without one, a UTC window");
    }

    // CPython 3.13, _load_schedule_payload: null, a string and a missing key are no schedules; a mapping raises SystemExit.
    [Fact]
    public void GuiManifestReaderFindsEntriesAsTheSchedulerDoes()
    {
        foreach (var text in new[] { """{"schedules": null}""", """{"schedules": "abc"}""", """{"schedules": 0}""", "{}", "false" })
        {
            Write(Path.Combine("Profiles", "schedules.json"), text);
            ScheduleStore.ListSchedules(_tmp.FullName, TestContext.Current.CancellationToken).Schedules.Should().BeEmpty(text);
        }

        Write(Path.Combine("Profiles", "schedules.json"), """{"schedules": {"a": 1}}""");
        FluentActions.Invoking(() => ScheduleStore.ListSchedules(_tmp.FullName, TestContext.Current.CancellationToken))
            .Should().Throw<CommandExitException>().WithMessage("Schedules payload must be an array of schedule entries.");
        Write(Path.Combine("Profiles", "schedules.json"), "{\"schedules\": [1,]}");
        FluentActions.Invoking(() => ScheduleStore.ListSchedules(_tmp.FullName, TestContext.Current.CancellationToken))
            .Should().Throw<CommandExitException>().WithMessage("Failed to parse schedules from *: invalid JSON document");
    }

    // CPython 3.13: Path("<tmp>/new/../sub/state.json").parent.mkdir(parents=True, exist_ok=True) creates new and sub, and the state is written.
    [Fact]
    public void WriteScheduleStateCreatesParentsAsTheKernelReachesThem()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "'..' after a missing directory is resolved by the kernel on POSIX");
        Write(Path.Combine("Profiles", "schedules.json"), """[{"name": "a", "profile": "p", "every": "1h", "start_at": "2025-01-01T00:00:00Z"}]""");

        ScheduleCommands.Due("2025-01-02T00:00:00Z", _tmp.FullName, statePath: $"{_tmp.FullName}/new/../sub/state.json").Should().ContainSingle();

        File.Exists(Path.Combine(_tmp.FullName, "sub", "state.json")).Should().BeTrue();
        Directory.Exists(Path.Combine(_tmp.FullName, "new")).Should().BeTrue();
        Directory.EnumerateDirectories(Path.Combine(_tmp.FullName, "new")).Should().BeEmpty();
    }

    private static object? Decode(string json)
    {
        PythonJson.TryLoads(json, out var value).Should().BeTrue();
        return value;
    }

    [Fact]
    public void GuiCardsKeepTheJsonTypesOfNumericIntervalsAndMetadata()
    {
        Write(
            Path.Combine("Profiles", "schedules.json"),
            """{"schedules": [{"name": "n", "profile": "p", "every": 90, "metadata": {"count": 5, "flag": true, "none": null, "list": [1, "a"], "text": "5"}}]}""");
        ScheduleCommands.List(_tmp.FullName).Should().ContainSingle();

        var card = ScheduleStore.ListSchedules(_tmp.FullName, TestContext.Current.CancellationToken).Schedules.Should().ContainSingle().Subject;
        card.Every.Should().Be("90");
        card.Metadata.Should().Equal(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["count"] = "5",
            ["flag"] = "True",
            ["none"] = string.Empty,
            ["list"] = "[1, \"a\"]",
            ["text"] = "5",
        });
        ScheduleStore.ValidationError(card).Should().BeNull();

        ScheduleStore.SaveSchedules([card], _tmp.FullName, TestContext.Current.CancellationToken);
        var saved = File.ReadAllText(Path.Combine(_tmp.FullName, "Profiles", "schedules.json"));
        PythonJson.TryLoads(saved, out var payload).Should().BeTrue();
        PythonRepr.Repr(payload).Should().Be(
            "{'schedules': [{'name': 'n', 'profile': 'p', 'every': 90, 'metadata': {'count': 5, 'flag': True, 'none': None, 'list': [1, 'a'], 'text': '5'}}]}");

        card.Every = "120";
        card.Metadata["count"] = "6";
        ScheduleStore.ValidationError(card).Should().Be("Unsupported interval fragment near: 120");
        card.Every = "2m";
        ScheduleStore.SaveSchedules([card], _tmp.FullName, TestContext.Current.CancellationToken);
        PythonJson.TryLoads(File.ReadAllText(Path.Combine(_tmp.FullName, "Profiles", "schedules.json")), out payload).Should().BeTrue();
        PythonRepr.Repr(payload).Should().Be(
            "{'schedules': [{'name': 'n', 'profile': 'p', 'every': '2m', 'metadata': {'count': '6', 'flag': True, 'none': None, 'list': [1, 'a'], 'text': '5'}}]}");
    }

    // CPython 3.13, run_profiles_cli schedule list reads each manifest: metadata nested past System.Text.Json's default depth, NaN and
    // Infinity, and unpaired surrogates in a name, a metadata key and a metadata value. Each loads as a card, and a load and an unchanged save
    // leave the listing as it was.
    [Theory]
    [InlineData("deep")]
    [InlineData("floats")]
    [InlineData("surrogates")]
    public void GuiCardsLoadEveryManifestTheSchedulerReads(string kind)
    {
        var entry = kind switch
        {
            "deep" => "{\"name\": \"a\", \"profile\": \"p\", \"every\": \"1h\", \"start_at\": \"2025-03-01T00:00:00Z\", \"metadata\": {\"deep\": " + new string('[', 70) + new string(']', 70) + "}}",
            "floats" => "{\"name\": \"a\", \"profile\": \"p\", \"every\": 3600.0, \"start_at\": \"2025-03-01T00:00:00Z\", \"metadata\": {\"n\": NaN, \"i\": [Infinity, -Infinity, 1e2]}}",
            _ => "{\"name\": \"\\ud800x\", \"profile\": \"p\", \"every\": \"1h\", \"start_at\": \"2025-03-01T00:00:00Z\", \"metadata\": {\"\\ud800\": \"v\", \"k\": \"\\udc00\"}}",
        };
        var manifest = Write(Path.Combine("Profiles", "schedules.json"), "{\"schedules\": [" + entry + "]}");
        var before = PythonRepr.Repr(ScheduleCommands.List(_tmp.FullName).ToList());
        var stateBefore = PythonRepr.Repr(ScheduleCommands.Due("2025-03-02T00:00:00Z", _tmp.FullName).ToList());
        File.Delete(Path.Combine(_tmp.FullName, "Profiles", "scheduler-state.json"));

        var card = ScheduleStore.ListSchedules(_tmp.FullName, TestContext.Current.CancellationToken).Schedules.Should().ContainSingle().Subject;
        ScheduleStore.ValidationError(card).Should().BeNull();
        ScheduleStore.SaveSchedules([card], _tmp.FullName, TestContext.Current.CancellationToken);

        PythonRepr.Repr(ScheduleCommands.List(_tmp.FullName).ToList()).Should().Be(before);
        PythonRepr.Repr(ScheduleCommands.Due("2025-03-02T00:00:00Z", _tmp.FullName).ToList()).Should().Be(stateBefore);
        File.ReadAllText(manifest).Should().NotContain("\ufffd");
    }

    // CPython 3.13, run_profiles_cli schedule due --at 2025-03-02T00:00:00Z over [b, a, C] (and over [z, "junk", y]) with one start_at:
    // the due runs come in manifest order. A load and an unchanged save keep that order.
    [Theory]
    [InlineData("{\"schedules\": [{\"name\": \"b\", {0}}, {\"name\": \"a\", {0}}, {\"name\": \"C\", {0}}]}", "b,a,C")]
    [InlineData("[{\"name\": \"z\", {0}}, \"junk\", {\"name\": \"y\", {0}}]", "z,y")]
    public void GuiLoadAndSaveKeepsTheOrderOfRunsDueTogether(string template, string expected)
    {
        const string fields = "\"profile\": \"p\", \"every\": \"1h\", \"start_at\": \"2025-03-01T00:00:00Z\"";
        Write(Path.Combine("Profiles", "schedules.json"), template.Replace("{0}", fields, StringComparison.Ordinal));
        var statePath = Path.Combine(_tmp.FullName, "state.json");
        string DueNames()
        {
            File.Delete(statePath);
            return string.Join(',', ScheduleCommands.Due("2025-03-02T00:00:00Z", _tmp.FullName, statePath: statePath)
                .Select(run => (string)((IReadOnlyDictionary<string, object?>)run!)["name"]!));
        }

        DueNames().Should().Be(expected);
        var cards = ScheduleStore.ListSchedules(_tmp.FullName, TestContext.Current.CancellationToken).Schedules;
        cards.Select(card => card.Name).Should().Equal(expected.Split(','));
        ScheduleStore.SaveSchedules(cards, _tmp.FullName, TestContext.Current.CancellationToken);
        DueNames().Should().Be(expected);
    }

    // CPython 3.13 _write_schedule_state over the same tree: path.parent.mkdir(parents=True, exist_ok=True) names the directory whose
    // os.mkdir failed, write_text the state path, both as str(Path) spells them.
    [Theory]
    [InlineData("afile/../state.json", "NotADirectoryError", "[Errno 20] Not a directory: '{root}/afile/..'")]
    [InlineData("afile/state.json", "FileExistsError", "[Errno 17] File exists: '{root}/afile'")]
    [InlineData("filelink/state.json", "FileExistsError", "[Errno 17] File exists: '{root}/filelink'")]
    [InlineData("dangling/state.json", "FileExistsError", "[Errno 17] File exists: '{root}/dangling'")]
    [InlineData("loop/state.json", "FileExistsError", "[Errno 17] File exists: '{root}/loop'")]
    [InlineData("afile/x/state.json", "NotADirectoryError", "[Errno 20] Not a directory: '{root}/afile/x'")]
    [InlineData("new/..", "IsADirectoryError", "[Errno 21] Is a directory: '{root}/new/..'")]
    [InlineData("sub/", "IsADirectoryError", "[Errno 21] Is a directory: '{root}/sub'")]
    [InlineData("missing/../sub", "IsADirectoryError", "[Errno 21] Is a directory: '{root}/missing/../sub'")]
    [InlineData("locked/new/state.json", "PermissionError", "[Errno 13] Permission denied: '{root}/locked/new'")]
    [InlineData("locked/state.json", "PermissionError", "[Errno 13] Permission denied: '{root}/locked/state.json'")]
    [InlineData("/proc/self/nonexistent/x.json", "FileNotFoundError", "[Errno 2] No such file or directory: '/proc/self/nonexistent'")]
    public void WriteScheduleStateRaisesPythonsOSErrorForAStatePathItCannotWrite(string state, string type, string message)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("errno text and '..' after a link are Linux kernel behaviour");
            return;
        }

        Assert.SkipWhen(state.StartsWith("locked/", StringComparison.Ordinal) && string.Equals(Environment.UserName, "root", StringComparison.Ordinal), "root ignores directory modes");
        var root = StateTree();
        try
        {
            var path = state.StartsWith('/') ? state : $"{root}/{state}";
            var write = () => ScheduleStore.WriteScheduleState(new ProfileScheduler(), path);

            var raised = write.Should().Throw<IOException>().Which;
            raised.Message.Should().Be(message.Replace("{root}", root, StringComparison.Ordinal));
            PythonOSError.TypeName(raised.HResult).Should().Be(type);
        }
        finally
        {
            File.SetUnixFileMode(Path.Combine(root, "locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // The same CPython calls that succeed: every directory is created where the kernel reaches it.
    [Fact]
    public void WriteScheduleStateCreatesTheParentsTheKernelReaches()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("'..' after a link is Linux kernel behaviour");
            return;
        }

        var root = StateTree();
        File.SetUnixFileMode(Path.Combine(root, "locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var before = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.Ordinal);

        foreach (var state in new[] { "new2/../q/r/../s.json", "shortcut/../made/state.json", "a//b/./c/state.json" })
        {
            ScheduleStore.WriteScheduleState(new ProfileScheduler(), $"{root}/{state}");
        }

        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Where(entry => !before.Contains(entry))
            .Select(entry => Path.GetRelativePath(root, entry)).Order(StringComparer.Ordinal)
            .Should().Equal("a", "a/b", "a/b/c", "a/b/c/state.json", "new2", "q", "q/r", "q/s.json", "real/made", "real/made/state.json");
        File.ReadAllText(Path.Combine(root, "q", "s.json")).Should().Be("{}\n");
    }

    // afile, filelink -> afile, dangling -> nowhere, loop -> loop, sub/, real/app/, shortcut -> real/app and locked/ (mode 555).
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private string StateTree()
    {
        var root = Path.Combine(_tmp.FullName, "state-tree");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "real", "app"));
        File.WriteAllText(Path.Combine(root, "afile"), "a");
        File.CreateSymbolicLink(Path.Combine(root, "filelink"), "afile");
        File.CreateSymbolicLink(Path.Combine(root, "dangling"), "nowhere");
        File.CreateSymbolicLink(Path.Combine(root, "loop"), "loop");
        Directory.CreateSymbolicLink(Path.Combine(root, "shortcut"), "real/app");
        var locked = Directory.CreateDirectory(Path.Combine(root, "locked"));
        File.SetUnixFileMode(locked.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return root;
    }
}
