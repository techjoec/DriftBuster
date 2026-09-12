using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests;

/// <summary>Fixture and repository paths independent of the working directory.</summary>
public static class RepoPaths
{
    public static string Root { get; } = RepositoryRoot.Require();

    public static string Fixtures(params string[] segments)
        => Path.Combine(new[] { Root, "fixtures" }.Concat(segments).ToArray());
}
