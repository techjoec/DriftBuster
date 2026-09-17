using System.Text;

using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure.EngineRe;

namespace DriftBuster.Backend.Tests.Hunt;

/// <summary>
/// Hunt walk behaviour: the install-path rule on Windows paths, unreadable files skipped and counted, a missing root, and cancellation.
/// </summary>
[Collection(HuntSeamCollection.Name)]
public sealed class HuntPortFixesTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-hunt-fixes-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void InstallPathRuleProducesAPlanTransform()
    {
        var target = Path.Combine(_tmp.FullName, "install.txt");
        File.WriteAllText(target, "InstallPath = C:\\Program Files\\Vendor\\App\n", new UTF8Encoding(false));

        var hits = HuntEngine.HuntPath(target, HuntRules.Default, cancellationToken: TestContext.Current.CancellationToken).Hits;

        hits.Should().ContainSingle();
        hits[0].Rule.Name.Should().Be("install-path");
        HuntEngine.BuildPlanTransforms(hits).Single().Value.Should().Be(@"C:\Program Files");
    }

    [Fact]
    public void UnreadableFileIsSkippedAndCounted()
    {
        if (OperatingSystem.IsWindows() || string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
        {
            return;
        }

        var locked = Path.Combine(_tmp.FullName, "a-locked.txt");
        File.WriteAllText(locked, "server host: locked.corp.local\n");
        File.WriteAllText(Path.Combine(_tmp.FullName, "b-open.txt"), "server host: open.corp.local\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var result = HuntEngine.HuntPath(_tmp.FullName, HuntRules.Default, cancellationToken: TestContext.Current.CancellationToken);

            result.UnreadableFiles.Should().Equal(locked);
            result.Hits.Select(hit => Path.GetFileName(hit.Path)).Should().Equal("b-open.txt");
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void MissingRootYieldsNoHits()
    {
        var missing = Path.Combine(_tmp.FullName, "missing");

        var result = HuntEngine.HuntPath(missing, HuntRules.Default, cancellationToken: TestContext.Current.CancellationToken);

        result.Hits.Should().BeEmpty();
        result.UnreadableFiles.Should().BeEmpty();
        result.RootDirectory.Should().Be(_tmp.FullName);
    }

    [Fact]
    public void CancellationStopsTheWalk()
    {
        File.WriteAllText(Path.Combine(_tmp.FullName, "x.txt"), "server host: x.corp.local\n");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var hunt = () => HuntEngine.HuntPath(_tmp.FullName, HuntRules.Default, cancellationToken: cancelled.Token);

        hunt.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void ASlowPatternSearchIsNeverCountedAsUnreadableAndStopsWhenCancelled()
    {
        var target = Path.Combine(_tmp.FullName, "slow.txt");
        File.WriteAllText(target, new string('x', 64) + "\n", new UTF8Encoding(false));
        var slow = new HuntRule("slow", "exponential backtracking", patterns: ["(x+x+)+y"]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        var hunt = () => HuntEngine.HuntPath(_tmp.FullName, [slow], cancellationToken: cancellation.Token);

        hunt.Should().Throw<OperationCanceledException>();
    }
}
