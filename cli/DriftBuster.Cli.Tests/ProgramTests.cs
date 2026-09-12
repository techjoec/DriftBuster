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
}
