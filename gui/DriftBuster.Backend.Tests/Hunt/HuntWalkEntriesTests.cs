using DriftBuster.Backend.Hunt;

namespace DriftBuster.Backend.Tests.Hunt;

/// <summary>
/// Cancellation while the hunt's glob walk is still listing directories.
/// </summary>
[Collection(HuntSeamCollection.Name)]
public sealed class HuntWalkEntriesTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-hunt-walk-");

    public void Dispose() => _tmp.Delete(recursive: true);

    // Eight links back to the root under a wildcard glob (wildcard parts follow symlinked directories) make a
    // generated tree of 8^12 directories to list: the hunt only returns if cancellation is honoured while the walk lists them.
    [Fact(Timeout = 60_000)]
    public async Task CancellationIsHonouredWhileTheGlobWalkListsDirectories()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        for (var index = 0; index < 8; index++)
        {
            File.CreateSymbolicLink(Path.Combine(_tmp.FullName, $"loop{index}"), ".");
        }

        // The literal last part is joined without listing, so the walk yields nothing and holds no results while it runs.
        var glob = string.Join('/', Enumerable.Repeat("*", 12)) + "/absent.ini";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var hunt = Task.Run(() => HuntEngine.HuntPath(_tmp.FullName, HuntRules.Default, glob, cancellationToken: cancellation.Token), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        hunt.IsCompleted.Should().BeFalse("the walk cannot finish 8^12 listings in 200 ms");

        await cancellation.CancelAsync();

        var outcome = () => hunt;
        await outcome.Should().ThrowAsync<OperationCanceledException>();
    }
}
