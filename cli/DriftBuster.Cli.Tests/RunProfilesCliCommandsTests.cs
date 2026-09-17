using System.Text;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// Mirror of tests/cli/test_run_profiles_cli_commands.py through <c>driftbuster profile</c> (<c>run_profiles_cli.main(argv)</c>, reached
/// from <c>python -m driftbuster.run_profiles_cli</c>, <c>run_profiles.main</c> and <c>python -m driftbuster.cli run-profile</c>).
/// Python calls the handlers with a hand-built <c>argparse.Namespace</c>; here the same values go through argv.
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
        shown.Json().GetProperty("options").EnumerateObject().Select(option => $"{option.Name}={option.Value.GetString()}").Should().Equal("baz=qux", "foo=bar");

        var refused = CliInvocation.Invoke("profile", "--base-dir", _tmp.FullName, "create", "--name", "bad", "--option", "invalid");
        refused.ExitCode.Should().Be(1);
        refused.Err.Should().Be("Invalid option format: 'invalid'. Use key=value." + Environment.NewLine);
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
        scanner.EnumerateObject().Select(property => property.Name).Should().Equal("ignore_patterns", "ignore_rules");
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

    /// <summary><c>Path(args.profile).read_text()</c> on a missing file: <c>open()</c>'s <c>FileNotFoundError</c> text naming the path.</summary>
    [Fact]
    public void RunWithMissingProfileFileReportsPythonsError()
    {
        var missing = Path.Combine(_tmp.FullName, "nope.json");

        var run = CliInvocation.Invoke("profile", "--base-dir", _tmp.FullName, "run", "--profile", missing);

        run.ExitCode.Should().Be(1);
        run.Out.Should().BeEmpty();
        run.Err.Should().Be($"FileNotFoundError: [Errno 2] No such file or directory: {DriftBuster.Backend.Infrastructure.PythonRepr.StrRepr(missing)}" + Environment.NewLine);
    }

    /// <summary>Python's <c>main</c> prints help and returns 1 when no handler was parsed; the console tool refuses a missing subcommand as a parse error.</summary>
    [Fact]
    public void MainReturnsErrorWhenNoCommand()
    {
        var run = CliInvocation.Invoke("profile");

        run.ExitCode.Should().NotBe(0);
        run.Err.Should().NotBeEmpty();
    }

    /// <summary><c>run_profiles.main(["list"])</c> forwards to the run profile command line: <c>driftbuster profile list</c>.</summary>
    [Fact]
    public void RunProfilesModuleMain()
    {
        var parse = Program.BuildRootCommand().Parse(["profile", "list"]);

        parse.Errors.Should().BeEmpty();
        parse.CommandResult.Command.Name.Should().Be("list");
        parse.CommandResult.Parent.Should().BeOfType<System.CommandLine.Parsing.CommandResult>()
            .Which.Command.Name.Should().Be("profile");
    }
}
