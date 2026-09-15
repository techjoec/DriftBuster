using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Scheduling;
using DriftBuster.Backend.Tests.Infrastructure;

using static DriftBuster.Backend.Tests.Scheduling.ScheduleOracleData;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>
/// <see cref="ScheduleWindow"/>, <see cref="ScheduleSpec"/> and <see cref="ProfileScheduler"/> against CPython on <c>Data/schedule_cases.json</c>:
/// <c>align</c> and <c>contains</c> for daytime, overnight, gap-straddling and fold-straddling windows at quarter hours around every 2025 and
/// 2040 transition; <c>initial_run</c> and twelve <c>next_after</c> steps for several intervals and windows from three hours before each 2025
/// transition; <c>from_dict</c> on valid and invalid payloads; and due / mark_complete / skip_until sequences whose state snapshots are
/// compared after every step. The scheduler class swaps <see cref="ProfileScheduler.Now"/>, so it runs alone.
/// </summary>
[Collection(ProcessWideSeamCollection.Name)]
public sealed class SchedulerParityTests
{
    [Fact]
    public void WindowAlignAndContainsMatchCPython()
    {
        var failures = new List<string>();
        foreach (var entry in Cases("windows"))
        {
            var zone = (string)entry["zone"]!;
            foreach (var item in List(entry["rows"]))
            {
                var row = List(item);
                var window = ScheduleWindow.FromDict(Payload(("start", row[0]), ("end", row[1]), ("timezone", zone)));
                var candidate = PythonDateTime.FromIsoFormat((string)row[2]!);
                var aligned = window.Align(candidate).IsoFormat();
                var contains = window.Contains(candidate);
                if (!string.Equals(aligned, (string)row[3]!, StringComparison.Ordinal) || contains != (bool)row[4]!)
                {
                    failures.Add($"{zone} {row[0]}-{row[1]} at {row[2]}: got {aligned} {contains}, expected {row[3]} {row[4]}");
                }
            }
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public void InitialRunAndNextAfterChainsMatchCPython()
    {
        var failures = new List<string>();
        foreach (var entry in Cases("chains"))
        {
            var spec = ScheduleSpec.FromDict(Map(entry["payload"]));
            var moment = spec.InitialRun();
            var runs = new List<string> { moment.IsoFormat() };
            for (var step = 0; step < 12; step++)
            {
                moment = spec.NextAfter(moment);
                runs.Add(moment.IsoFormat());
            }

            var expected = List(entry["runs"]).Cast<string>().ToList();
            if (!runs.SequenceEqual(expected, StringComparer.Ordinal))
            {
                failures.Add($"{PythonRepr.Repr(entry["payload"])}: got [{string.Join(", ", runs)}], expected [{string.Join(", ", expected)}]");
            }
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public void FromDictMatchesCPython()
    {
        var failures = new List<string>();
        foreach (var entry in Cases("specs"))
        {
            if (Mismatch(entry, () => ScheduleSpec.FromDict(Map(entry["payload"])), SpecMismatch) is { } failure)
            {
                failures.Add($"{PythonRepr.Repr(entry["payload"])}: {failure}");
            }
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public void SchedulerSequencesMatchCPython()
    {
        var original = ProfileScheduler.Now;
        ProfileScheduler.Now = () => PythonDateTime.Create(2025, 3, 8, 23, 59, 30, 250000, PythonFixedOffset.Utc);
        try
        {
            foreach (var entry in Cases("schedulers"))
            {
                RunScenario(entry);
            }
        }
        finally
        {
            ProfileScheduler.Now = original;
        }
    }

    private static void RunScenario(OrderedDictionary<string, object?> entry)
    {
        var scenario = Map(entry["scenario"]);
        var specs = List(scenario["specs"]).Select(item => ScheduleSpec.FromDict(Map(item))).ToList();
        var scheduler = new ProfileScheduler(specs);
        var steps = List(entry["steps"]).Select(Map).ToList();
        var start = PythonDateTime.FromIsoFormat((string)scenario["start"]!);
        var reference = start;
        var end = start.Add(PythonTimeDelta.FromFloats(hours: Long(scenario["hours"])));
        var index = 0;
        while (reference <= end)
        {
            var expected = steps[index++];
            var due = scheduler.Due(reference);
            due.Select(run => $"{run.Name}@{run.ScheduledFor.IsoFormat()}")
                .Should().Equal(List(expected["due"]).Select(item => $"{List(item)[0]}@{List(item)[1]}"), $"{entry["name"]} at {expected["at"]}");
            foreach (var run in due)
            {
                scheduler.MarkComplete(run.Name, run.ScheduledFor.Add(PythonTimeDelta.FromFloats(minutes: Long(scenario["complete_after_minutes"]))));
            }

            PythonRepr.Repr(scheduler.SnapshotState()).Should().Be(PythonRepr.Repr(expected["state"]), $"{entry["name"]} state at {expected["at"]}");
            reference = reference.Add(PythonTimeDelta.FromFloats(minutes: Long(scenario["step_minutes"])));
        }

        var resume = start.Add(PythonTimeDelta.FromFloats(hours: 7, minutes: 13));
        foreach (var spec in specs)
        {
            scheduler.SkipUntil(spec.Name, resume);
        }

        var last = steps[index];
        resume.IsoFormat().Should().Be((string)last["skip"]!);
        PythonRepr.Repr(scheduler.SnapshotState()).Should().Be(PythonRepr.Repr(last["state"]), $"{entry["name"]} after skip_until");
        index.Should().Be(steps.Count - 1);
    }

    private static string? SpecMismatch(ScheduleSpec spec, object? expected)
    {
        var fields = Map(expected);
        var actual = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = spec.Name,
            ["profile"] = spec.Profile,
            ["interval"] = spec.Interval.Repr(),
            ["start_at"] = spec.StartAt?.IsoFormat(),
            ["window"] = spec.Window is null ? null : new List<object?> { spec.Window.Start.IsoFormat(), spec.Window.End.IsoFormat(), spec.Window.Timezone.Name },
            ["tags"] = spec.Tags.Cast<object?>().ToList(),
            ["metadata"] = new OrderedDictionary<string, object?>(spec.Metadata, StringComparer.Ordinal),
        };
        var wanted = new OrderedDictionary<string, object?>(fields, StringComparer.Ordinal) { ["interval"] = List(fields["interval"])[4] };
        return string.Equals(PythonRepr.Repr(actual), PythonRepr.Repr(wanted), StringComparison.Ordinal)
            ? null
            : $"got {PythonRepr.Repr(actual)}, expected {PythonRepr.Repr(wanted)}";
    }

    internal static OrderedDictionary<string, object?> Payload(params (string Key, object? Value)[] items)
    {
        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            payload[key] = value;
        }

        return payload;
    }
}
