using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Cli.Commands;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// <c>driftbuster detection-profile</c>. The payload assertions of the library half also live in DetectionProfileCommandsTests; these assert the exit codes, stdout, <c>--output</c>,
/// <c>--indent</c> and <c>--sort-keys</c> of the console tool. The class swaps <see cref="DetectionProfileCommands.FromDict"/>, so it
/// runs outside the parallel tests.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public sealed class ProfileCliTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-profile-cli-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_tmp.FullName, name);
        File.WriteAllText(path, text, Utf8);
        return path;
    }

    [Fact]
    public void ProfileCliSummary()
    {
        var storePath = Write("store.json", """{"profiles": [{"name": "prod", "configs": [{"id": "cfg1", "path": "configs/app.config"}]}]}""");

        var run = CliInvocation.Invoke("detection-profile", "summary", storePath);

        run.ExitCode.Should().Be(0, run.Err);
        var data = run.Json();
        data.GetProperty("total_profiles").GetInt32().Should().Be(1);
        data.GetProperty("total_configs").GetInt32().Should().Be(1);
    }

    [Fact]
    public void ProfileCliDiff()
    {
        var baseline = Write("baseline.json", """{"total_profiles": 1, "total_configs": 1, "profiles": []}""");
        var current = Write("current.json", """{"total_profiles": 2, "total_configs": 3, "profiles": []}""");

        var run = CliInvocation.Invoke("detection-profile", "diff", baseline, current);

        run.ExitCode.Should().Be(0, run.Err);
        run.Json().GetProperty("totals").GetProperty("current").GetProperty("profiles").GetInt32().Should().Be(2);
    }

    [Fact]
    public void ProfileCliHuntBridge()
    {
        var storePath = Write(
            "store.json",
            """
            {"profiles": [{"name": "prod", "tags": ["prod"], "configs": [{"id": "cfg1", "path": "configs/appsettings.json",
              "expected_format": "json", "expected_variant": "structured-settings-json"}]}]}
            """);
        var huntPath = Write(
            "hunts.json",
            """
            [{"rule": {"name": "server-name", "description": "", "token_name": "server"}, "relative_path": "configs/appsettings.json",
              "path": "dummy", "line_number": 1, "excerpt": "server: dev"}]
            """);

        var run = CliInvocation.Invoke("detection-profile", "hunt-bridge", storePath, huntPath, "--tag", "prod");

        run.ExitCode.Should().Be(0, run.Err);
        run.Json().GetProperty("items")[0].GetProperty("profiles")[0].GetProperty("profile").GetString().Should().Be("prod");
    }

    [Fact]
    public void ProfileCliSummaryWritesOutputFile()
    {
        var storePath = Write("store.json", """{"profiles": [{"name": "prod", "configs": [{"id": "cfg1"}]}]}""");
        var output = Path.Combine(_tmp.FullName, "summary.json");

        var run = CliInvocation.Invoke("detection-profile", "summary", storePath, "--output", output, "--indent", "0", "--sort-keys");

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Should().BeEmpty();
        var text = File.ReadAllText(output, Utf8);
        text.Should().StartWith("{\"profiles\": [").And.EndWith("}" + Environment.NewLine);
        System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("total_profiles").GetInt32().Should().Be(1);
    }

    [Fact]
    public void StoreFromPayloadIgnoresInvalidEntries()
    {
        var original = DetectionProfileCommands.FromDict;
        DetectionProfileCommands.FromDict = payload => throw new InvalidDataException("fallback");
        try
        {
            var storePath = Write("store.json", """{"profiles": ["invalid", {"name": "demo", "configs": ["skip", {"id": "cfg", "path": "config.json"}]}]}""");

            var run = CliInvocation.Invoke("detection-profile", "summary", storePath);

            run.ExitCode.Should().Be(0, run.Err);
            run.Json().GetProperty("total_profiles").GetInt32().Should().Be(1);
        }
        finally
        {
            DetectionProfileCommands.FromDict = original;
        }
    }

    [Fact]
    public void HandleHuntBridgeValidatesPayload()
    {
        var storePath = Write("store.json", """{"profiles": []}""");

        var refused = CliInvocation.Invoke("detection-profile", "hunt-bridge", storePath, storePath, "--root", _tmp.FullName);

        refused.ExitCode.Should().Be(1);
        refused.Out.Should().BeEmpty();
        refused.Err.Should().Be("error: Hunt payload must be a JSON array of hunt hits." + Environment.NewLine);

        var outside = Path.Combine(_tmp.Parent!.FullName, "outside.txt").Replace("\\", "\\\\", StringComparison.Ordinal);
        var huntPath = Write(
            "hunts.json",
            $$"""
            [{"rule": {"name": "server", "description": ""}, "path": "{{outside}}", "line_number": 1, "excerpt": "server"},
             {"rule": {"name": "missing", "description": ""}, "line_number": 2, "excerpt": "data"}]
            """);

        var accepted = CliInvocation.Invoke("detection-profile", "hunt-bridge", storePath, huntPath, "--root", _tmp.FullName);

        accepted.ExitCode.Should().Be(0, accepted.Err);
        var items = accepted.Json().GetProperty("items");
        items.GetArrayLength().Should().Be(2);
        items[0].GetProperty("relative_path").GetString().Should().Be("outside.txt");
    }

    /// <summary><c>--indent</c> of zero or less writes one line, and <c>--sort-keys</c> orders the keys.</summary>
    [Fact]
    public void WriteJsonHonoursIndentAndSortKeys()
    {
        using var stdout = new StringWriter();
        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["b"] = 1, ["a"] = new List<object?> { "é" } };

        DetectionProfileCommand.WriteJson(payload, new DetectionProfileCommand.OutputOptions(-3, SortKeys: true, Output: null), stdout);
        DetectionProfileCommand.WriteJson(payload, new DetectionProfileCommand.OutputOptions(3, SortKeys: false, Output: null), stdout);

        stdout.ToString().Should().Be("{\"a\": [\"\\u00e9\"], \"b\": 1}\n{\n   \"b\": 1,\n   \"a\": [\n      \"\\u00e9\"\n   ]\n}\n");
    }
}
