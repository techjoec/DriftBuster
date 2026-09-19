using System.CommandLine;

using DriftBuster.Cli;

namespace DriftBuster.Cli.Tests;

public sealed class ProgramTests
{
    [Fact]
    public void Root_command_parses_help()
    {
        var result = Program.BuildRootCommand().Parse("--help");
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Help_lists_every_command()
    {
        var run = CliInvocation.Invoke("--help");

        run.ExitCode.Should().Be(0);
        foreach (var name in new[] { "scan", "diff", "hunt", "multi-server", "profile", "detection-profile", "schedule", "registry-scan", "sql-export", "report", "capture" })
        {
            run.Out.Should().Contain($"  {name}");
        }
    }

    [Fact]
    public void A_parse_error_exits_two_with_the_error_on_stderr()
    {
        var run = CliInvocation.Invoke("scan");

        run.ExitCode.Should().Be(2);
        run.Out.Should().BeEmpty();
        run.Err.Should().StartWith("driftbuster: error: ");
    }

    [Fact]
    public void A_missing_command_exits_two()
    {
        CliInvocation.Invoke().ExitCode.Should().Be(2);
    }

    /// <summary>An argument starting with "@" is a value: response files are off.</summary>
    [Fact]
    public void An_at_sign_argument_is_a_value_not_a_response_file()
    {
        var run = CliInvocation.Invoke("scan", "@missing-response-file");

        run.ExitCode.Should().Be(2);
        run.Err.Should().Be("driftbuster: error: Path does not exist: @missing-response-file" + Environment.NewLine);
    }

    /// <summary>An exception the command does not handle ends it with exit code 1 and one line naming the error.</summary>
    [Fact]
    public void An_unhandled_exception_exits_one_with_its_error_name()
    {
        var tmp = Directory.CreateTempSubdirectory("driftbuster-cli-program-");
        try
        {
            // A directory given as the profile file fails to open: an error the command does not expect.
            var run = CliInvocation.Invoke("profile", "run", "--profile", tmp.FullName, "--base-dir", tmp.FullName);

            run.ExitCode.Should().Be(1);
            run.Err.Should().StartWith("UnauthorizedAccessException: ");
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }
}
