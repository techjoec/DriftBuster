using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Tests.Profiles.Run;
using DriftBuster.Backend.Tests.Secrets;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>
/// <see cref="RunProfileCommands"/> secret overrides. The console tool's tests cover option parsing, list, create, show and run. The class
/// shares the rule cache collection because a run loads the packaged secret rules.
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
    public void SecretOverridesMergeCleanedValuesWithoutRepeats()
    {
        var source = Path.Combine(_tmp.FullName, "file.txt");
        File.WriteAllText(source, "payload", Utf8);
        var profile = new RunProfile(
            "merge",
            sources: RunProfilesTests.Sources(source),
            secretScanner: RunProfilesTests.Map(("ignore_rules", new List<object?> { " a ", "b" }), ("ruleset", RunProfilesTests.Map(("version", "1")))));

        EngineRepr.Repr(RunProfileCommands.ApplySecretOverrides(profile, [null, " ", "b"], null).ToDict()["secret_scanner"])
            .Should().Be("{'ignore_rules': ['a', 'b'], 'ruleset': {'version': '1'}}");
        var merged = RunProfileCommands.ApplySecretOverrides(profile, ["c", "a", "c"], [" x ", "x"]);
        EngineRepr.Repr(merged.ToDict()["secret_scanner"]).Should().Be("{'ignore_rules': ['a', 'b', 'c'], 'ruleset': {'version': '1'}, 'ignore_patterns': ['x']}");
        RunProfileCommands.ApplySecretOverrides(profile, [], [" "]).Should().BeSameAs(profile);
        RunProfileCommands.CleanSecretValues([" z ", null, "", "z", "y"]).Should().Equal("z", "y");
    }
}
