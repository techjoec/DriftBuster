namespace DriftBuster.Backend.Tests;

public sealed class RepoPathsTests
{
    [Fact]
    public void Root_contains_the_solution_and_fixtures()
    {
        File.Exists(Path.Combine(RepoPaths.Root, "DriftBuster.sln")).Should().BeTrue();
        Directory.Exists(RepoPaths.Fixtures("multi-server", "server01")).Should().BeTrue();
    }
}
