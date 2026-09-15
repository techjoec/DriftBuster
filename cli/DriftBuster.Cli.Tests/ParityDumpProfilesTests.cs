using System.CommandLine;
using System.Text;
using System.Text.Json;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Scheduling;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// The profile-store, profile-diff, run-profile and schedule surfaces of <c>parity-dump</c>. Expected lines are
/// tools/parity/py_dump.py's output over the same inputs.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public sealed class ParityDumpProfilesTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-parity-profiles-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Write(string relative, string content)
    {
        var path = Path.Combine([_tmp.FullName, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Invoke(params string[] args)
    {
        var output = new StringWriter();
        var configuration = new InvocationConfiguration { Output = output, Error = new StringWriter() };
        Program.BuildRootCommand().Parse(args).Invoke(configuration).Should().Be(0);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle();
        return lines[0];
    }

    [Fact]
    public void ProfileStore_prints_summary_lookups_and_round_trip_as_python_does()
    {
        var payload = Write(
            "store.json",
            """{"profiles": [{"name": "web", "tags": ["env:prod"], "configs": [{"id": "app", "path": "config/app.json", "metadata": {"b": 1, "a": 2}}, {"id": "any", "path_glob": "*.config"}]}]}""");

        var line = Invoke("parity-dump", "profile-store", payload, "--tags", "env:prod", "--path", "config/app.json");

        var document = JsonDocument.Parse(line).RootElement;
        document.GetProperty("applicable_profiles").EnumerateArray().Select(name => name.GetString()).Should().Equal("web");
        document.GetProperty("matching_configs").EnumerateArray().Select(match => match.GetProperty("config").GetString()).Should().Equal("app");
        document.GetProperty("find_config").EnumerateArray().Select(entry => entry.GetProperty("matches").GetArrayLength()).Should().Equal(1, 1, 0);
        line.Should().Contain(
            "\"summary\": {\"profiles\": [{\"config_count\": 2, \"config_ids\": [\"app\", \"any\"], \"description\": null, \"name\": \"web\", \"tags\": [\"env:prod\"]}], \"total_configs\": 2, \"total_profiles\": 1}, \"to_dict\"");
        line.Should().Contain(
            "[\"round_trip\", [[\"profiles\", [[[\"name\", null], [\"description\", null], [\"tags\", [null]], [\"metadata\", []], [\"configs\", [[[\"id\", null], [\"path\", null], [\"path_glob\", null], [\"application\", null], [\"version\", null], [\"branch\", null], [\"tags\", []], [\"expected_format\", null], [\"expected_variant\", null], [\"metadata\", [[\"b\", null], [\"a\", null]]]]");
    }

    [Fact]
    public void ProfileStore_prints_the_python_exception_of_a_payload_the_store_refuses()
    {
        var payload = Write("dup.json", """{"profiles": [{"name": "a"}, {"name": "a"}]}""");

        Invoke("parity-dump", "profile-store", payload).Should().Be(
            """{"error": {"message": "Profile 'a' is already registered", "type": "ValueError"}, "key_order": [["error", [["type", null], ["message", null]]]]}""");
    }

    [Fact]
    public void ProfileDiff_prints_diff_summary_snapshots_and_errors()
    {
        var baseline = Write("base.json", """{"profiles": [{"name": "web", "config_count": 1, "config_ids": ["app"]}]}""");
        var current = Write("cur.json", """{"profiles": [{"name": "web", "config_ids": ["app", "new"]}, {"name": "db", "config_ids": []}]}""");

        Invoke("parity-dump", "profile-diff", baseline, current).Should().Be(
            """{"diff": {"added_profiles": ["db"], "changed_profiles": [{"added_config_ids": ["new"], "baseline_config_count": 1, "current_config_count": 2, "name": "web", "removed_config_ids": []}], "removed_profiles": [], "totals": {"baseline": {"configs": 1, "profiles": 1}, "current": {"configs": 2, "profiles": 2}}}, "key_order": [["diff", [["totals", [["baseline", [["profiles", null], ["configs", null]]], ["current", [["profiles", null], ["configs", null]]]]], ["added_profiles", [null]], ["removed_profiles", []], ["changed_profiles", [[["name", null], ["baseline_config_count", null], ["current_config_count", null], ["added_config_ids", [null]], ["removed_config_ids", []]]]]]]]}""");
        JsonDocument.Parse(Invoke("parity-dump", "profile-diff", Path.Combine(_tmp.FullName, "absent.json"), current)).RootElement
            .GetProperty("error").GetProperty("type").GetString().Should().Be("ValueError");
    }

    [Fact]
    public void RunProfile_runs_in_a_copy_of_the_workdir_and_lists_every_profile_file()
    {
        Write("wd/config/app.env", "password=SuperSecret1234\nplain\n");
        var profile = Write("run.json", """{"name": "run one", "sources": ["config"]}""");
        var scratch = Path.Combine(_tmp.FullName, "scratch");
        var before = Environment.CurrentDirectory;

        var document = JsonDocument.Parse(Invoke("parity-dump", "run-profile", profile, Path.Combine(_tmp.FullName, "wd"), "--scratch", scratch)).RootElement;

        Environment.CurrentDirectory.Should().Be(before);
        Directory.Exists(scratch).Should().BeFalse();
        document.GetProperty("result").GetProperty("output_dir").GetString().Should().Be("<workdir>/Profiles/run-one/raw/20250102T030405Z");
        var collected = document.GetProperty("collected")[0];
        collected.GetProperty("relative").GetString().Should().Be("app.env");
        collected.GetProperty("directory").ValueKind.Should().Be(JsonValueKind.Null);
        collected.GetProperty("sha256").GetString().Should().Be("4fb73526a661454d20a35c9ab0cb673c75b9c762961d70f5576b5d815ded1127");
        var profiles = document.GetProperty("profiles").EnumerateArray().ToList();
        profiles.Select(entry => entry.GetProperty("path").GetString()).Should().Equal(
            "Profiles/run-one/profile.json", "Profiles/run-one/raw/20250102T030405Z/metadata.json", "Profiles/run-one/raw/20250102T030405Z/source_00/app.env");
        profiles[0].GetProperty("size").GetInt32().Should().Be(140);
        profiles[1].GetProperty("text").GetString().Should().Contain("\"destination\": \"<workdir>/Profiles/run-one/raw/20250102T030405Z/source_00/app.env\"");
        profiles[2].TryGetProperty("text", out _).Should().BeFalse();
        document.TryGetProperty("redaction_guard", out _).Should().BeFalse();
    }

    [Fact]
    public void RunProfile_reports_structured_alias_directories_guards_and_errors()
    {
        Write("wd/config/app.env", "token secret\n");
        Write("wd/config/skip.bak", "ignored\n");
        var structured = Write("structured.json", """{"name": "aliased", "sources": [{"path": "config", "alias": "Cfg", "exclude": ["*.bak"]}]}""");
        var looping = Write(
            "loop.json",
            """{"name": "loop", "sources": ["config/app.env"], "secret_scanner": {"ruleset": {"version": "loop", "rules": [{"name": "SelfMatching", "pattern": "secret", "flags": "i"}]}}}""");
        var missing = Write("missing.json", """{"name": "missing", "sources": ["nope"]}""");
        var workdir = Path.Combine(_tmp.FullName, "wd");

        var aliased = JsonDocument.Parse(Invoke("parity-dump", "run-profile", structured, workdir)).RootElement;
        var guarded = JsonDocument.Parse(Invoke("parity-dump", "run-profile", looping, workdir)).RootElement;
        var failed = JsonDocument.Parse(Invoke("parity-dump", "run-profile", missing, Path.Combine(_tmp.FullName, "absent"))).RootElement;

        aliased.GetProperty("collected").EnumerateArray().Select(entry => $"{entry.GetProperty("directory").GetString()}/{entry.GetProperty("relative").GetString()}")
            .Should().Equal("Cfg/app.env");
        guarded.GetProperty("redaction_guard")[0].GetProperty("rule").GetString().Should().Be("SelfMatching");
        failed.GetProperty("error").GetProperty("type").GetString().Should().Be("FileNotFoundError");
        failed.GetProperty("error").GetProperty("message").GetString().Should().Be("Path does not exist: <workdir>/nope");
        failed.GetProperty("profiles").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void RunProfile_copies_symlinks_as_links_and_removes_the_scratch_directory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need elevation on Windows");
        Write("wd/tree/a.txt", "a\n");
        Write("wd/outside/o.txt", "o\n");
        File.CreateSymbolicLink(Path.Combine(_tmp.FullName, "wd", "tree", "lnkfile"), "a.txt");
        Directory.CreateSymbolicLink(Path.Combine(_tmp.FullName, "wd", "tree", "lnkdir"), "../outside");
        File.CreateSymbolicLink(Path.Combine(_tmp.FullName, "wd", "tree", "dangling"), "nowhere");
        var stringProfile = Write("links.json", """{"name": "links", "sources": ["tree"]}""");
        var structuredProfile = Write("structured-links.json", """{"name": "links", "sources": [{"path": "tree/lnkfile", "alias": "l"}, {"path": "tree", "alias": "t"}]}""");
        var scratch = Path.Combine(_tmp.FullName, "scratch");

        var plain = JsonDocument.Parse(Invoke("parity-dump", "run-profile", stringProfile, Path.Combine(_tmp.FullName, "wd"), "--scratch", scratch)).RootElement;
        Directory.Exists(scratch).Should().BeFalse();
        var structured = JsonDocument.Parse(Invoke("parity-dump", "run-profile", structuredProfile, Path.Combine(_tmp.FullName, "wd"), "--scratch", scratch)).RootElement;
        Directory.Exists(scratch).Should().BeFalse();

        plain.GetProperty("collected").EnumerateArray().Select(entry => entry.GetProperty("relative").GetString()).Should().Equal("a.txt", "lnkfile");
        structured.GetProperty("collected").EnumerateArray().Select(entry => $"{entry.GetProperty("directory").GetString()}/{entry.GetProperty("relative").GetString()}")
            .Should().Equal("t/a.txt", "t/lnkfile");
    }

    [Fact]
    public void Schedule_prints_the_command_payload_and_the_state_it_leaves()
    {
        var config = Write(
            "sched.json",
            """{"schedules": [{"name": "nightly", "profile": "p", "every": "1d", "start_at": "2025-03-01T11:00:00Z", "window": {"start": "22:00", "end": "02:00", "timezone": "America/Chicago"}}]}""");
        var absent = Path.Combine(_tmp.FullName, "none.json");

        Invoke("parity-dump", "schedule", config, absent, "due").Should().Be(
            """{"key_order": [["output", []], ["state", null]], "output": [], "state": "{\n  \"nightly\": {\n    \"next_run\": \"2025-03-02T04:00:00+00:00\",\n    \"pending\": null\n  }\n}\n"}""");
        Invoke("parity-dump", "schedule", config, absent, "mark-complete", "--name", "nightly").Should().Be(
            """{"error": {"message": "Schedule nightly is not pending.", "type": "SystemExit"}, "key_order": [["error", [["type", null], ["message", null]]], ["state", null]], "state": null}""");
        var state = Write("state.json", "{\"nightly\": {\"next_run\": \"2025-03-01T00:00:00Z\"}}\r\n");
        var due = JsonDocument.Parse(Invoke("parity-dump", "schedule", config, state, "due", "--at", "2025-03-02T00:00:00Z", "--now", "2025-01-01T00:00:00+00:00")).RootElement;
        due.GetProperty("output")[0].GetProperty("scheduled_for").GetString().Should().Be("2025-03-01T00:00:00+00:00");
        JsonDocument.Parse(Invoke("parity-dump", "schedule", config, state, "skip-until", "--name", "nightly", "--resume-at", "2025-03-05T03:00:00Z")).RootElement
            .GetProperty("output").GetProperty("next_run").GetString().Should().Be("2025-03-05T04:00:00+00:00");
        JsonDocument.Parse(Invoke("parity-dump", "schedule", config, state, "list")).RootElement
            .GetProperty("output")[0].GetProperty("window").GetProperty("timezone").GetString().Should().Be("America/Chicago");
        File.ReadAllText(state).Should().EndWith("\r\n");
    }

    // py_dump.py reads the state file with Path.read_text(encoding="utf-8"), which keeps a byte order mark as U+FEFF.
    [Fact]
    public void Schedule_prints_the_state_text_with_its_byte_order_mark()
    {
        var config = Write("sched.json", """{"schedules": [{"name": "a", "profile": "p", "every": "1h", "start_at": "2025-03-01T00:00:00Z"}]}""");
        var state = Path.Combine(_tmp.FullName, "bom-state.json");
        File.WriteAllBytes(state, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{}")]);

        var record = JsonDocument.Parse(Invoke("parity-dump", "schedule", config, state, "list")).RootElement;

        record.GetProperty("state").GetString().Should().Be("\ufeff{}");
        record.GetProperty("error").GetProperty("type").GetString().Should().Be("SystemExit");
    }

    // py_dump.py over the same steps: argparse ends run_profiles_cli.main with SystemExit(2) before the manifest is read.
    [Theory]
    [InlineData("mark-complete")]
    [InlineData("skip-until", "--name", "a")]
    [InlineData("skip-until", "--resume-at", "2025-01-01")]
    [InlineData("list", "--name", "a")]
    [InlineData("due", "--at", "2025-01-01", "--completed-at", "x")]
    [InlineData("mark-complete", "--name", "a", "--at", "2025-01-01")]
    public void Schedule_reports_arguments_argparse_refuses_as_a_system_exit(params string[] step)
    {
        var config = Write("missing-dir/never-read.json", "not json");
        Invoke(["parity-dump", "schedule", config, Path.Combine(_tmp.FullName, "none.json"), .. step]).Should().Be(
            """{"error": {"message": "2", "type": "SystemExit"}, "key_order": [["error", [["type", null], ["message", null]]], ["state", null]], "state": null}""");
    }

    [Fact]
    public void PythonErrorName_names_the_python_exception_each_port_exception_stands_for()
    {
        Exceptions("x").Select(ParityDump.PythonErrorName).Should().Equal(
            "SystemExit", "ScheduleError", "ValueError", "TypeError", "IndexError", "KeyError", "FileNotFoundError", "OverflowError",
            "AttributeError", "UnicodeDecodeError", "IsADirectoryError", "PermissionError", "PermissionError", "InvalidDataException", "IOException",
            "InvalidOperationException", "RecursionError", "FileExistsError", "NotADirectoryError", "FileNotFoundError", "PermissionError", "OSError");
    }

    private static Exception[] Exceptions(string message) =>
    [
        new CommandExitException(message), new ScheduleException(message), new PythonValueException(message, nameof(message)),
        new PythonTypeException(message, nameof(message)), new PythonIndexException(nameof(message), message), new KeyNotFoundException(message),
        new FileNotFoundException(message), new OverflowException(message), new PythonAttributeException(message),
        new PythonUnicodeDecodeException(message), PythonOSError.Create(PythonOSError.IsADirectory, message),
        PythonOSError.Create(PythonOSError.PermissionDenied, message), new UnauthorizedAccessException(message), new InvalidDataException(message), new IOException(message),
        new InvalidOperationException(message), new PythonRecursionException(message), PythonOSError.Create(PythonOSError.FileExists, message),
        PythonOSError.Create(PythonOSError.NotADirectory, message), PythonOSError.Create(PythonOSError.NoSuchFile, message),
        PythonOSError.Create(PythonOSError.OperationNotPermitted, message), PythonOSError.Create(36, message),
    ];
}
