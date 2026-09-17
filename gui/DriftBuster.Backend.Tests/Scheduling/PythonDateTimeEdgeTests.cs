using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Scheduling;
using DriftBuster.Backend.Tests.Infrastructure;
using DriftBuster.Backend.Tests.Profiles.Run;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>
/// The <c>datetime</c>, <c>timezone</c> and scheduling behaviours the oracle data does not reach: constructor checks, naive and mixed
/// comparisons, fixed offset names, a <see cref="PythonTimeDelta"/> interval, an empty timestamp, a state path that is a directory and
/// the profile loader the manifest specs carry. Messages are CPython 3.13's.
/// </summary>
public sealed class PythonDateTimeEdgeTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-datetime-edges-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void ConstructorChecksFieldsInPythonsOrder()
    {
        FluentActions.Invoking(() => PythonDateTime.Create(2025, 1, 1, fold: 2)).Should().Throw<PythonValueException>().WithMessage("fold must be either 0 or 1");
        FluentActions.Invoking(() => PythonDateTime.Create(2025, 1, 1, microsecond: 1_000_000)).Should().Throw<PythonValueException>().WithMessage("microsecond must be in 0..999999");
        FluentActions.Invoking(() => PythonDateTime.Create(2025, 2, 29)).Should().Throw<PythonValueException>().WithMessage("day is out of range for month");
        PythonDateTime.Create(2025, 1, 1, 1, 2, 3, 4).ToString().Should().Be("2025-01-01T01:02:03.000004");
    }

    [Fact]
    public void ComparisonsFollowPythonsAwareAndNaiveRules()
    {
        var naive = PythonDateTime.Create(2025, 1, 1, 12);
        var utc = naive.WithTz(PythonFixedOffset.Utc);
        var plusOne = PythonDateTime.FromIsoFormat("2025-01-01T13:00:00+01:00");
        var chicago = PythonDateTime.Create(2025, 1, 1, 6, tz: PythonZoneInfo.Create("America/Chicago"));

        FluentActions.Invoking(() => naive < utc).Should().Throw<PythonTypeException>().WithMessage("can't compare offset-naive and offset-aware datetimes");
        FluentActions.Invoking(() => naive.AsTimeZone(PythonFixedOffset.Utc)).Should().Throw<NotSupportedException>();
        utc.CompareWith(plusOne).Should().Be(0);
        utc.CompareWith(chicago).Should().Be(0);
        (plusOne > PythonDateTime.FromIsoFormat("2025-01-01T12:30:00+01:00")).Should().BeTrue();
        (plusOne >= chicago).Should().BeTrue();
        (naive > PythonDateTime.Create(2025, 1, 1, 11)).Should().BeTrue();
        PythonDateTime.FromIsoFormat("2025-01-01T13:00:00+00:00").CompareWith(plusOne).Should().Be(1);
    }

    [Fact]
    public void FixedOffsetsAreNamedAsTimezoneStr()
    {
        PythonFixedOffset.Create(PythonTimeDelta.FromMicroseconds(0)).Should().BeSameAs(PythonFixedOffset.Utc);
        PythonFixedOffset.Utc.Name.Should().Be("UTC");
        PythonDateTime.FromIsoFormat("2025-01-01T00:00:00+05:30").Tz!.Name.Should().Be("UTC+05:30");
        PythonDateTime.FromIsoFormat("2025-01-01T00:00:00-00:00:01").Tz!.Name.Should().Be("UTC-00:00:01");
        PythonDateTime.FromIsoFormat("2025-01-01T00:00:00+00:00:01.5").Tz!.Name.Should().Be("UTC+00:00:01.500000");
        PythonDateTime.FromIsoFormat("2025-01-01T00:00:00-00:00:00.000001").Tz.Should().BeSameAs(PythonFixedOffset.Utc);
        var window = new ScheduleWindow(ScheduleParsing.ParseTime("01:00"), ScheduleParsing.ParseTime("02:00"));
        window.Timezone.Should().BeSameAs(PythonFixedOffset.Utc);
    }

    [Fact]
    public void IntervalsAndTimestampsOutsideTheOracleInputs()
    {
        ScheduleParsing.ParseInterval(PythonTimeDelta.FromFloats(minutes: 5)).Should().Be(PythonTimeDelta.FromFloats(seconds: 300));
        FluentActions.Invoking(() => ScheduleParsing.ParseInterval(PythonTimeDelta.FromMicroseconds(-1))).Should().Throw<ScheduleException>().WithMessage("Interval must be positive.");
        FluentActions.Invoking(() => ScheduleParsing.ParseTimestamp(string.Empty)).Should().Throw<ScheduleException>().WithMessage("Timestamp payload must not be empty.");
        FluentActions.Invoking(() => new ScheduleSpec("n", "p", default)).Should().Throw<ScheduleException>().WithMessage("Interval must be positive.");
    }

    [Fact]
    public void StateFileThatIsADirectoryRaisesOpensErrorForADirectory()
    {
        var state = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "state.json")).FullName;
        FluentActions.Invoking(() => ScheduleStore.LoadScheduleState(state))
            .Should().Throw<IOException>().Which.Message.Should().Be(OSErrorTexts.DirectoryOpen(state));
    }

    [Fact]
    public void ManifestSpecsLoadProfilesFromTheBaseDirectory()
    {
        var source = Path.Combine(_tmp.FullName, "config.txt");
        File.WriteAllText(source, "baseline");
        RunProfileStore.SaveProfile(new RunProfile("nightly", sources: RunProfilesTests.Sources(source)), _tmp.FullName);

        var specs = ScheduleStore.BuildScheduleSpecs([SchedulerParityTests.Payload(("name", "n"), ("profile", "nightly"), ("every", "1h"))], _tmp.FullName);
        specs.Single().LoadProfile().Sources.Select(item => item.Path).Should().Equal(source);
        FluentActions.Invoking(() => ScheduleStore.BuildScheduleSpecs([SchedulerParityTests.Payload(("name", "n"), ("profile", "p"))], _tmp.FullName))
            .Should().Throw<CommandExitException>().WithMessage("Schedule entries require name, profile, and every");
    }
}
