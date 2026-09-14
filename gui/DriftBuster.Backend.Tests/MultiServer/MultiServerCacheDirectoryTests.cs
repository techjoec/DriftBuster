using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Backend.Tests.MultiServer;

/// <summary>
/// The <c>_resolve_cache_dir</c> tests of <c>tests/multi_server/test_multi_server.py</c>. They set <c>DRIFTBUSTER_DATA_ROOT</c> as
/// the Python tests monkeypatch it, so they run outside the parallel collections. Python reads the legacy cache relative to the
/// working directory (the test changes directory); the port takes the repository root explicitly.
/// </summary>
[Collection(DataRootEnvironmentCollection.Name)]
public sealed class MultiServerCacheDirectoryTests : IDisposable
{
    private const string DataRootVariable = "DRIFTBUSTER_DATA_ROOT";
    private const string XdgDataHomeVariable = "XDG_DATA_HOME";

    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-multi-server-cache-dir-");
    private readonly string? _originalDataRoot = Environment.GetEnvironmentVariable(DataRootVariable);
    private readonly string? _originalXdgDataHome = Environment.GetEnvironmentVariable(XdgDataHomeVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DataRootVariable, _originalDataRoot);
        Environment.SetEnvironmentVariable(XdgDataHomeVariable, _originalXdgDataHome);
        _tmp.Delete(recursive: true);
    }

    [Fact]
    public void ResolveCacheDirUsesDataRootEnv()
    {
        var dataRoot = Path.Combine(_tmp.FullName, "data-root");
        Environment.SetEnvironmentVariable(DataRootVariable, dataRoot);

        var cacheDir = DiffCache.ResolveCacheDirectory(null, null);

        cacheDir.Should().Be(Resolve(Path.Combine(dataRoot, "cache", "diffs")));
        Directory.Exists(cacheDir).Should().BeTrue();
    }

    // Path.resolve(): the absolute path with every symlink followed.
    private static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        return PythonPath.ResolvePhysicalPath(full) ?? full;
    }

    // The same rule through a data root reached by a symlink: _resolve_data_root() resolves it, so the cache path names the target.
    [Fact]
    public void ResolveCacheDirResolvesADataRootReachedThroughASymlink()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "symlinks are created without privileges only on posix");
        var target = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "real-data-root")).FullName;
        var link = Path.Combine(_tmp.FullName, "linked-data-root");
        Directory.CreateSymbolicLink(link, target);
        Environment.SetEnvironmentVariable(DataRootVariable, link);

        var cacheDir = DiffCache.ResolveCacheDirectory(null, null);

        cacheDir.Should().Be(Path.Combine(target, "cache", "diffs"));
        Directory.Exists(cacheDir).Should().BeTrue();
    }

    [Fact]
    public void ResolveCacheDirMigratesLegacyCache()
    {
        var dataRoot = Path.Combine(_tmp.FullName, "data-root");
        var repoRoot = Path.Combine(_tmp.FullName, "repo");
        var legacyDir = Path.Combine(repoRoot, "artifacts", "cache", "diffs");
        Directory.CreateDirectory(legacyDir);
        var legacyFile = Path.Combine(legacyDir, "legacy.json");
        File.WriteAllText(legacyFile, "{}");
        Environment.SetEnvironmentVariable(DataRootVariable, dataRoot);

        var cacheDir = DiffCache.ResolveCacheDirectory(null, repoRoot);

        var migrated = Path.Combine(cacheDir, Path.GetFileName(legacyFile));
        File.Exists(migrated).Should().BeTrue();
        File.ReadAllText(migrated).Should().Be("{}");
    }

    // _migrate_legacy_cache returns when any(destination.iterdir()).
    [Fact]
    public void ResolveCacheDirSkipsMigrationWhenTheCacheHoldsEntries()
    {
        var dataRoot = Path.Combine(_tmp.FullName, "data-root");
        var repoRoot = Path.Combine(_tmp.FullName, "repo");
        var legacyDir = Path.Combine(repoRoot, "artifacts", "cache", "diffs");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "legacy.json"), "{}");
        var destination = Path.Combine(dataRoot, "cache", "diffs");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "current.json"), "{}");
        Environment.SetEnvironmentVariable(DataRootVariable, dataRoot);

        var cacheDir = DiffCache.ResolveCacheDirectory(null, repoRoot);

        Directory.GetFiles(cacheDir).Select(Path.GetFileName).Should().Equal("current.json");
    }

    [Fact]
    public void ExplicitCacheDirIsCreatedAndResolved()
    {
        var explicitDir = Path.Combine(_tmp.FullName, "explicit", "cache");

        var cacheDir = DiffCache.ResolveCacheDirectory(explicitDir, null);

        cacheDir.Should().Be(Path.GetFullPath(explicitDir));
        Directory.Exists(cacheDir).Should().BeTrue();
    }
    // _resolve_cache_dir(cache_dir): mkdir and resolve() walk the path as the kernel does, so "link/.." is the parent of the link's
    // target and the lexical parent of the link is never created.
    [Fact]
    public void ExplicitCacheDirWithDotDotAfterASymlinkResolvesPhysically()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "'..' after a symlink resolves physically on POSIX kernels");
        var target = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "real", "inner")).FullName;
        var outer = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "outer")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(outer, "link"), target);

        var cacheDir = DiffCache.ResolveCacheDirectory($"{outer}/link/../cache", null);

        cacheDir.Should().Be(Path.Combine(_tmp.FullName, "real", "cache"));
        Directory.Exists(cacheDir).Should().BeTrue();
        Directory.Exists(Path.Combine(outer, "cache")).Should().BeFalse();
    }

    // The GUI facade sends its cache directory through the explicit branch, as the Python bridge sent cache_dir: a data root reached
    // through a symlink names the link's target.
    [Fact]
    public void TheFacadeCacheDirectoryResolvesADataRootReachedThroughASymlink()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "symlinks are created without privileges only on posix");
        var target = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "real-data-root")).FullName;
        var link = Path.Combine(_tmp.FullName, "linked-data-root");
        Directory.CreateSymbolicLink(link, target);
        var repoRoot = Path.Combine(_tmp.FullName, "repo");
        var legacyDir = Directory.CreateDirectory(Path.Combine(repoRoot, "artifacts", "cache", "diffs")).FullName;
        File.WriteAllText(Path.Combine(legacyDir, "legacy.json"), "{}");
        Environment.SetEnvironmentVariable(DataRootVariable, link);

        var cacheDir = DriftbusterBackend.PrepareMultiServerCacheDirectory(repoRoot);

        cacheDir.Should().Be(Path.Combine(target, "cache", "diffs"));
        File.ReadAllText(Path.Combine(cacheDir, "legacy.json")).Should().Be("{}");
    }

    // XDG_DATA_HOME keeps a ".." the data root helper never normalises; the facade's cache directory, the legacy migration and the
    // runner's entries all land where the kernel resolves it.
    [Fact]
    public async Task TheFacadeScanWritesTheCacheWhereTheKernelResolvesTheDataRoot()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "XDG_DATA_HOME and '..' after a symlink are POSIX cases");
        var target = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "real", "inner")).FullName;
        var outer = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "outer")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(outer, "link"), target);
        var repoRoot = Path.Combine(_tmp.FullName, "repo");
        var legacyDir = Directory.CreateDirectory(Path.Combine(repoRoot, "artifacts", "cache", "diffs")).FullName;
        File.WriteAllText(Path.Combine(legacyDir, "legacy.json"), "{}");
        var hostRoot = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "host")).FullName;
        File.WriteAllText(Path.Combine(hostRoot, "app.ini"), "[core]\nname = a\n");
        Environment.SetEnvironmentVariable(DataRootVariable, null);
        Environment.SetEnvironmentVariable(XdgDataHomeVariable, $"{outer}/link/..");
        var physical = Path.Combine(_tmp.FullName, "real", "DriftBuster", "cache", "diffs");

        DriftbusterBackend.PrepareMultiServerCacheDirectory(repoRoot).Should().Be(physical);
        var response = await new DriftbusterBackend().RunServerScansAsync(
            [new ServerScanPlan { HostId = "host", Label = "host", Roots = [hostRoot] }],
            progress: null,
            TestContext.Current.CancellationToken);

        response.Results.Should().ContainSingle().Which.Status.Should().Be(ServerScanStatus.Succeeded);
        Directory.GetFiles(physical).Select(Path.GetFileName).Should().Contain("legacy.json").And.HaveCountGreaterThan(1);
    }
}
