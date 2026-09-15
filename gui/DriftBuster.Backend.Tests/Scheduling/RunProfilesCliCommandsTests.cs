using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Tests.Profiles.Run;
using DriftBuster.Backend.Tests.Secrets;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>
/// Mirror of the library half of tests/cli/test_run_profiles_cli_commands.py. Python calls <c>_parse_options</c>, <c>_list_profiles</c>,
/// <c>_create</c>, <c>_show</c> and <c>_run</c> with an <c>argparse.Namespace</c> and reads stdout; here the same arguments go through
/// <see cref="RunProfileCommands"/> and the assertions are made on the lines, payloads and results the commands print from.
/// <c>test_main_entrypoint_dispatches</c>, <c>test_main_returns_error_when_no_command</c> and <c>test_run_profiles_module_main</c> test
/// <c>build_parser</c> / <c>main</c> dispatch and belong to the console tool. <c>_run</c> loads the packaged secret rules, so the class
/// shares the rule cache collection.
/// </summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class RunProfilesCliCommandsTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SecretRuleCacheIsolation _isolation = new();
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-run-profiles-cli-");

    public void Dispose()
    {
        _isolation.Dispose();
        _tmp.Delete(recursive: true);
    }

    [Fact]
    public void ParseOptionsValidatesFormat()
    {
        var options = RunProfileCommands.ParseOptions(["foo=bar", "baz = qux "]);
        PythonRepr.Repr(options).Should().Be("{'foo': 'bar', 'baz': 'qux'}");

        FluentActions.Invoking(() => RunProfileCommands.ParseOptions(["invalid"]))
            .Should().Throw<CommandExitException>().WithMessage("Invalid option format: 'invalid'. Use key=value.");
    }

    [Fact]
    public void ListProfilesReportsEmpty()
    {
        var lines = RunProfileCommands.ListProfileLines(_tmp.FullName, TestContext.Current.CancellationToken);
        string.Join("\n", lines).Should().Contain("No profiles");
    }

    [Fact]
    public void CreateListShowAndRun()
    {
        var source = Path.Combine(_tmp.FullName, "config.ini");
        File.WriteAllText(source, "content", Utf8);
        var baseDir = _tmp.FullName;

        RunProfileCommands.Create("demo", "Example", [source], source, ["key=value"], ["Skip"], ["ALLOW"], baseDir);

        var listed = string.Join("\n", RunProfileCommands.ListProfileLines(baseDir, TestContext.Current.CancellationToken));
        listed.Should().Contain("demo");

        var payload = RunProfileCommands.Show("demo", baseDir);
        payload["name"].Should().Be("demo");
        PythonRepr.Repr(payload["options"]).Should().Be("{'key': 'value'}");
        // Python compares the mappings without order; the reloaded profile keeps profile.json's sorted key order, as CPython does.
        PythonRepr.Repr(payload["secret_scanner"]).Should().Be("{'ignore_patterns': ['ALLOW'], 'ignore_rules': ['Skip']}");

        var profileFile = Path.Combine(baseDir, "Profiles", "demo", "profile.json");
        var result = RunProfileCommands.Run(profileFile, null, baseDir, save: true, "20240101T000000Z", [], [], TestContext.Current.CancellationToken);
        $"Run saved to {result.OutputDir}".Should().Contain("Run saved");
    }

    [Fact]
    public void RunCommandLoadsByName()
    {
        var source = Path.Combine(_tmp.FullName, "file.txt");
        File.WriteAllText(source, "payload", Utf8);

        var profile = new RunProfile("cli", sources: RunProfilesTests.Sources(source));
        RunProfileStore.SaveProfile(profile, baseDir: _tmp.FullName);

        var result = RunProfileCommands.Run(null, "cli", _tmp.FullName, save: false, null, [], [], TestContext.Current.CancellationToken);
        $"Files collected: {result.Files.Count}".Should().Contain("Files collected");
        result.Files.Should().HaveCount(1);
    }

    [Fact]
    public void SecretOverridesMergeCleanedValuesWithoutRepeats()
    {
        var source = Path.Combine(_tmp.FullName, "file.txt");
        File.WriteAllText(source, "payload", Utf8);
        var profile = new RunProfile(
            "merge",
            sources: RunProfilesTests.Sources(source),
            secretScanner: RunProfilesTests.Map(("ignore_rules", new List<object?> { " a ", "b" }), ("ruleset", RunProfilesTests.Map(("version", "1")))));

        // CPython 3.13 on the same inputs.
        PythonRepr.Repr(RunProfileCommands.ApplySecretOverrides(profile, [null, " ", "b"], null).ToDict()["secret_scanner"])
            .Should().Be("{'ignore_rules': ['a', 'b'], 'ruleset': {'version': '1'}}");
        var merged = RunProfileCommands.ApplySecretOverrides(profile, ["c", "a", "c"], [" x ", "x"]);
        PythonRepr.Repr(merged.ToDict()["secret_scanner"]).Should().Be("{'ignore_rules': ['a', 'b', 'c'], 'ruleset': {'version': '1'}, 'ignore_patterns': ['x']}");
        RunProfileCommands.ApplySecretOverrides(profile, [], [" "]).Should().BeSameAs(profile);
        RunProfileCommands.CleanSecretValues([" z ", null, "", "z", "y"]).Should().Equal("z", "y");
    }

    // A structured profile hands build_context its options as the payload held them (a list's items are ignore patterns); an override applied
    // through the profile's own dict, whose options are str() text, must not turn ["5"] into the pattern "['5']".
    [Fact]
    public void SecretOverridesKeepTheRawOptionsOfAStructuredProfile()
    {
        var dataDir = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "data")).FullName;
        File.WriteAllText(Path.Combine(dataDir, "app.ini"), "password=SuperSecret1234 5True\n", Utf8);
        var profile = RunProfile.FromDict(RunProfilesTests.Map(
            ("name", "options"),
            ("sources", new List<object?> { RunProfilesTests.Map(("path", dataDir), ("alias", "data")) }),
            ("options", RunProfilesTests.Map(("secret_ignore_patterns", new List<object?> { "5" })))));

        var overridden = RunProfileCommands.ApplySecretOverrides(profile, ["NoSuchRule"], null);

        overridden.Should().NotBeSameAs(profile);
        overridden.SecretOptions.Should().BeEquivalentTo(profile.SecretOptions);
        overridden.SecretScanner["ignore_rules"].Should().BeEquivalentTo(new List<object?> { "NoSuchRule" });
        var result = RunProfileExecutor.ExecuteProfile(overridden, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: TestContext.Current.CancellationToken);
        ((IList<object?>)((IReadOnlyDictionary<string, object?>)result.Secrets!)["findings"]!).Should().BeEmpty();
    }
}
