using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Scheduling;
using DriftBuster.Backend.Tests.Profiles.Run;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>
/// Mirror of tests/scheduler/test_integration.py. Python invokes <c>run_profiles_cli.main(["--base-dir", tmp, "schedule", ...])</c> and parses
/// stdout; here each command runs through <see cref="ScheduleCommands"/> with the same base directory and arguments, and the assertions
/// on the printed payload and the state file are Python's. Printing and exit codes belong to the console tool.
/// </summary>
public sealed class ScheduleIntegrationTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-schedule-integration-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private static OrderedDictionary<string, object?> ReadJson(string path)
    {
        PythonJson.TryLoads(File.ReadAllText(path, Utf8), out var value).Should().BeTrue();
        return value.Should().BeOfType<OrderedDictionary<string, object?>>().Subject;
    }

    private static OrderedDictionary<string, object?> Entry(object? value) => value.Should().BeOfType<OrderedDictionary<string, object?>>().Subject;

    [Fact]
    public void ScheduleCommandsManageState()
    {
        var baseDir = _tmp.FullName;
        var sourceDir = Directory.CreateDirectory(Path.Combine(baseDir, "data"));
        var sourceFile = Path.Combine(sourceDir.FullName, "config.txt");
        File.WriteAllText(sourceFile, "baseline", Utf8);

        var profile = new RunProfile("nightly", sources: RunProfilesTests.Sources(sourceFile));
        RunProfileStore.SaveProfile(profile, baseDir: baseDir);

        var schedulesPath = Path.Combine(baseDir, "Profiles", "schedules.json");
        Directory.CreateDirectory(Path.GetDirectoryName(schedulesPath)!);
        File.WriteAllText(
            schedulesPath,
            """
            {
              "schedules": [
                {
                  "name": "nightly",
                  "profile": "nightly",
                  "every": "24h",
                  "start_at": "2025-01-01T00:00:00Z",
                  "metadata": {
                    "notes": "Rotation scheduled"
                  }
                }
              ]
            }

            """,
            Utf8);

        var duePayload = ScheduleCommands.Due("2025-01-02T00:00:00Z", baseDir);
        duePayload.Should().HaveCount(1);
        var dueEntry = Entry(duePayload[0]);
        dueEntry["name"].Should().Be("nightly");
        dueEntry["scheduled_for"].Should().Be("2025-01-01T00:00:00+00:00");

        var statePath = Path.Combine(Path.GetDirectoryName(schedulesPath)!, "scheduler-state.json");
        var state = ReadJson(statePath);
        Entry(state["nightly"])["pending"].Should().Be("2025-01-01T00:00:00+00:00");

        var completionPayload = ScheduleCommands.MarkComplete("nightly", "2025-01-01T00:00:00Z", baseDir);
        completionPayload["next_run"].Should().Be("2025-01-02T00:00:00+00:00");
        state = ReadJson(statePath);
        Entry(state["nightly"])["next_run"].Should().Be("2025-01-02T00:00:00+00:00");
        Entry(state["nightly"])["pending"].Should().BeNull();

        var skipPayload = ScheduleCommands.SkipUntil("nightly", "2025-01-05T09:30:00Z", baseDir);
        skipPayload["next_run"].Should().Be("2025-01-05T09:30:00+00:00");

        var listing = ScheduleCommands.List(baseDir);
        Entry(listing[0])["next_run"].Should().Be("2025-01-05T09:30:00+00:00");
    }
}
