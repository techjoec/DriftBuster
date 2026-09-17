using System.Text;
using System.Text.Json;

namespace DriftBuster.Cli.Tests;

/// <summary><c>driftbuster --base-dir TMP schedule ...</c> end to end.</summary>
public sealed class ScheduleIntegrationTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-schedule-cli-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private CliInvocation InvokeCli(params string[] args) => CliInvocation.Invoke(["schedule", "--base-dir", _tmp.FullName, .. args]);

    private static JsonElement ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path, Utf8)).RootElement.Clone();

    [Fact]
    public void ScheduleCommandsManageState()
    {
        var baseDir = _tmp.FullName;
        var sourceFile = Path.Combine(Directory.CreateDirectory(Path.Combine(baseDir, "data")).FullName, "config.txt");
        File.WriteAllText(sourceFile, "baseline", Utf8);
        CliInvocation.Invoke("profile", "--base-dir", baseDir, "create", "--name", "nightly", "--source", sourceFile).ExitCode.Should().Be(0);
        var schedulesPath = Path.Combine(baseDir, "Profiles", "schedules.json");
        File.WriteAllText(
            schedulesPath,
            """
            {
              "schedules": [
                {"name": "nightly", "profile": "nightly", "every": "24h", "start_at": "2025-01-01T00:00:00Z", "metadata": {"notes": "Rotation scheduled"}}
              ]
            }

            """,
            Utf8);

        var due = InvokeCli("due", "--at", "2025-01-02T00:00:00Z");
        due.ExitCode.Should().Be(0, due.Err);
        var duePayload = due.Json();
        duePayload.GetArrayLength().Should().Be(1);
        duePayload[0].GetProperty("name").GetString().Should().Be("nightly");
        duePayload[0].GetProperty("scheduled_for").GetString().Should().Be("2025-01-01T00:00:00+00:00");
        var statePath = Path.Combine(baseDir, "Profiles", "scheduler-state.json");
        ReadJson(statePath).GetProperty("nightly").GetProperty("pending").GetString().Should().Be("2025-01-01T00:00:00+00:00");

        var completion = InvokeCli("mark-complete", "--name", "nightly", "--completed-at", "2025-01-01T00:00:00Z");
        completion.ExitCode.Should().Be(0, completion.Err);
        completion.Json().GetProperty("next_run").GetString().Should().Be("2025-01-02T00:00:00+00:00");
        var state = ReadJson(statePath).GetProperty("nightly");
        state.GetProperty("next_run").GetString().Should().Be("2025-01-02T00:00:00+00:00");
        state.GetProperty("pending").ValueKind.Should().Be(JsonValueKind.Null);

        var skip = InvokeCli("skip-until", "--name", "nightly", "--resume-at", "2025-01-05T09:30:00Z");
        skip.ExitCode.Should().Be(0, skip.Err);
        skip.Json().GetProperty("next_run").GetString().Should().Be("2025-01-05T09:30:00+00:00");

        var listing = InvokeCli("list");
        listing.ExitCode.Should().Be(0, listing.Err);
        listing.Json()[0].GetProperty("next_run").GetString().Should().Be("2025-01-05T09:30:00+00:00");
    }

    /// <summary><c>raise SystemExit(message)</c> from the schedule commands: the message on stderr and exit code 1.</summary>
    [Fact]
    public void ScheduleErrorsExitOneWithTheMessage()
    {
        var run = InvokeCli("list");

        run.ExitCode.Should().Be(1);
        run.Out.Should().BeEmpty();
        run.Err.Should().Be($"Schedule manifest not found: {Path.Combine(_tmp.FullName, "Profiles", "schedules.json")}{Environment.NewLine}");
    }
}
