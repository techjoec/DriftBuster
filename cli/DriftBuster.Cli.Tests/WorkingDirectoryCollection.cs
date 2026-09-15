namespace DriftBuster.Cli.Tests;

/// <summary>Tests that change the process working directory run alone, after every parallel test.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkingDirectoryCollection
{
    public const string Name = "working directory";
}
