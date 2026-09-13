using DriftBuster.Backend.Hunt;

namespace DriftBuster.Backend.Tests.Hunt;

/// <summary>
/// Walk entries <c>hunt_path</c> meets outside ordinary files: FIFOs, sockets and devices (skipped unopened, as
/// <c>Path.is_file()</c> skips them), a file root with a trailing separator, a name the runtime cannot decode (listed as
/// unreadable) and cancellation while the glob walk is still listing directories.
/// </summary>
[Collection(HuntSeamCollection.Name)]
public sealed class HuntWalkEntriesTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-hunt-walk-");

    public void Dispose() => SpecialFiles.DeleteTree(_tmp);

    [Fact(Timeout = 30_000)]
    public async Task FifosSocketsAndDevicesAreSkippedWithoutBlocking()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "FIFOs and Unix sockets in a walked tree are a Linux case");
        using var special = SpecialFiles.Create(_tmp.FullName);
        File.WriteAllText(Path.Combine(_tmp.FullName, "regular.ini"), "server host=regular.corp.local\n");

        var result = await Task.Run(() => HuntEngine.HuntPath(_tmp.FullName, HuntRules.Default), TestContext.Current.CancellationToken);

        result.Hits.Select(hit => Path.GetFileName(hit.Path)).Distinct(StringComparer.Ordinal).Should().Equal("regular.ini");
        result.UnreadableFiles.Should().BeEmpty();
        foreach (var path in special.All)
        {
            var single = await Task.Run(() => HuntEngine.HuntPath(path, HuntRules.Default), TestContext.Current.CancellationToken);
            single.Hits.Should().BeEmpty(path);
            single.UnreadableFiles.Should().BeEmpty(path);
        }
    }

    [Fact]
    public void AFileRootWithATrailingSeparatorIsTheFile()
    {
        var file = Path.Combine(_tmp.FullName, "x.config");
        File.WriteAllText(file, "server host=x.corp.local\n");

        var result = HuntEngine.HuntPath(file + Path.DirectorySeparatorChar, HuntRules.Default, cancellationToken: TestContext.Current.CancellationToken);

        result.Hits.Should().NotBeEmpty();
        result.Hits.Select(hit => hit.Path).Distinct(StringComparer.Ordinal).Should().Equal(file);
        result.RootDirectory.Should().Be(_tmp.FullName);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnUndecodableNameIsListedAsUnreadable()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "file names are bytes only on Linux here");
        SpecialFiles.CreateUndecodableName(_tmp.FullName, "server host=bad.corp.local\n");
        File.WriteAllText(Path.Combine(_tmp.FullName, "good.ini"), "server host=good.corp.local\n");

        var result = await Task.Run(() => HuntEngine.HuntPath(_tmp.FullName, HuntRules.Default), TestContext.Current.CancellationToken);

        result.UnreadableFiles.Should().Equal(Path.Combine(_tmp.FullName, "bad-�.ini"));
        result.Hits.Select(hit => Path.GetFileName(hit.Path)).Distinct(StringComparer.Ordinal).Should().Equal("good.ini");
        var excluded = HuntEngine.HuntPath(_tmp.FullName, HuntRules.Default, excludePatterns: ["bad-*"], cancellationToken: TestContext.Current.CancellationToken);
        excluded.UnreadableFiles.Should().BeEmpty();
    }

    // Eight links back to the root under a wildcard glob (wildcard parts follow symlinked directories, as in Python) make a
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

    // CPython's hunt_path raises PermissionError from is_file() on rdir/x.conf and aborts; fix b lists the file as unreadable
    // and keeps hunting (an excluded one is not listed).
    [Fact]
    public void AFileWhoseStatIsRefusedIsListedAsUnreadable()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess, "search permission is observable only for a non-root Linux user");
        File.WriteAllText(Path.Combine(_tmp.FullName, "ok.ini"), "server host=ok.corp.local\n");
        var file = SpecialFiles.CreateUnsearchableDirectory(_tmp.FullName, "rdir", "server host=hidden.corp.local\n");

        var result = HuntEngine.HuntPath(_tmp.FullName, HuntRules.Default, cancellationToken: TestContext.Current.CancellationToken);
        var excluded = HuntEngine.HuntPath(_tmp.FullName, HuntRules.Default, excludePatterns: ["rdir/*"], cancellationToken: TestContext.Current.CancellationToken);

        result.UnreadableFiles.Should().Equal(file);
        result.Hits.Select(hit => Path.GetFileName(hit.Path)).Distinct(StringComparer.Ordinal).Should().Equal("ok.ini");
        excluded.UnreadableFiles.Should().BeEmpty();
    }

    // Platform limit (expected_divergences.md): CPython walks into a directory named d\xff and reports child.corp from its
    // child; the port cannot name the directory, lists the directory itself as unreadable and does not walk its subtree.
    [Fact(Timeout = 30_000)]
    public async Task AnUndecodableDirectoryIsListedAsUnreadableAndItsSubtreeIsNotWalked()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "file names are bytes only on Linux here");
        SpecialFiles.CreateUndecodableDirectory(_tmp.FullName, "server host=child.corp.local\n");
        File.WriteAllText(Path.Combine(_tmp.FullName, "good.ini"), "server host=good.corp.local\n");

        var result = await Task.Run(() => HuntEngine.HuntPath(_tmp.FullName, HuntRules.Default), TestContext.Current.CancellationToken);

        result.UnreadableFiles.Should().Equal(Path.Combine(_tmp.FullName, "d\uFFFD"));
        result.Hits.Select(hit => Path.GetFileName(hit.Path)).Distinct(StringComparer.Ordinal).Should().Equal("good.ini");
    }
}
