using System.Text;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// <c>driftbuster profile</c>: the run profile handlers, with every value passed through argv.
/// </summary>
public sealed class RunProfilesCliCommandsTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-run-profiles-cli-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void ParseOptionsValidatesFormat()
    {
        var source = Path.Combine(_tmp.FullName, "config.ini");
        File.WriteAllText(source, "content", Utf8);

        var accepted = CliInvocation.Invoke("profile", "--base-dir", _tmp.FullName, "create", "--name", "opts", "--source", source, "--option", "foo=bar", "--option", "baz = qux ");
        accepted.ExitCode.Should().Be(0, accepted.Err);
        var shown = CliInvocation.Invoke("profile", "--base-dir", _tmp.FullName, "show", "opts");
        shown.Json().GetProperty("options").EnumerateObject().Select(option => $"{option.Name}={option.Value.GetString()}").Should().Equal("foo=bar", "baz=qux");

        var refused = CliInvocation.Invoke("profile", "--base-dir", _tmp.FullName, "create", "--name", "bad", "--option", "invalid");
        refused.ExitCode.Should().Be(1);
        refused.Err.Should().Be("Invalid option 'invalid': use key=value." + Environment.NewLine);
    }

    [Fact]
    public void ListProfilesReportsEmpty()
    {
        var run = CliInvocation.Invoke("profile", "list", "--base-dir", _tmp.FullName);

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Should().Contain("No profiles");
    }

    [Fact]
    public void CreateListShowAndRun()
    {
        var source = Path.Combine(_tmp.FullName, "config.ini");
        File.WriteAllText(source, "content", Utf8);
        var baseDir = _tmp.FullName;

        CliInvocation.Invoke(
            "profile", "--base-dir", baseDir, "create", "--name", "demo", "--description", "Example", "--source", source, "--baseline", source,
            "--option", "key=value", "--secret-ignore-rule", "Skip", "--secret-ignore-pattern", "ALLOW").ExitCode.Should().Be(0);

        CliInvocation.Invoke("profile", "--base-dir", baseDir, "list").Out.Should().Contain("demo");

        var payload = CliInvocation.Invoke("profile", "--base-dir", baseDir, "show", "demo").Json();
        payload.GetProperty("name").GetString().Should().Be("demo");
        payload.GetProperty("options").GetProperty("key").GetString().Should().Be("value");
        payload.GetProperty("options").EnumerateObject().Should().HaveCount(1);
        var scanner = payload.GetProperty("secret_scanner");
        scanner.EnumerateObject().Select(property => property.Name).Should().Equal("ignore_rules", "ignore_patterns");
        scanner.GetProperty("ignore_rules").EnumerateArray().Select(value => value.GetString()).Should().Equal("Skip");
        scanner.GetProperty("ignore_patterns").EnumerateArray().Select(value => value.GetString()).Should().Equal("ALLOW");

        var profileFile = Path.Combine(baseDir, "Profiles", "demo", "profile.json");
        var run = CliInvocation.Invoke("profile", "--base-dir", baseDir, "run", "--profile", profileFile, "--save", "--timestamp", "20240101T000000Z");
        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Should().Contain("Run saved");
    }

    [Fact]
    public void RunCommandLoadsByName()
    {
        var source = Path.Combine(_tmp.FullName, "file.txt");
        File.WriteAllText(source, "payload", Utf8);
        CliInvocation.Invoke("profile", "--base-dir", _tmp.FullName, "create", "--name", "cli", "--source", source).ExitCode.Should().Be(0);

        var run = CliInvocation.Invoke("profile", "--base-dir", _tmp.FullName, "run", "--name", "cli");

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Should().Contain("Files collected");
    }

    [Fact]
    public void MainEntrypointDispatches()
    {
        var dummy = Path.Combine(_tmp.FullName, "dummy");
        File.WriteAllText(dummy, "data", Utf8);

        var run = CliInvocation.Invoke("profile", "--base-dir", _tmp.FullName, "create", "--name", "entry", "--source", dummy);

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Should().Be("Saved profile 'entry'" + Environment.NewLine);
    }
}
