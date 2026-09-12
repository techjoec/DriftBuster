using System.CommandLine;

namespace DriftBuster.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        return BuildRootCommand().Parse(args).Invoke();
    }

    public static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("DriftBuster configuration drift tooling.");
        root.Subcommands.Add(ParityDump.Build());
        return root;
    }
}
