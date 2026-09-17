using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Backend.Tests.MultiServer;

/// <summary>
/// The multi-server cache directory resolution. The tests set <c>DRIFTBUSTER_DATA_ROOT</c>, so they run
/// outside the parallel collections. The legacy cache is found under a repository root passed explicitly.
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

    // The absolute path with every symlink followed.
    private static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        return EnginePath.ResolvePhysicalPath(full) ?? full;
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

    [Fact]
    public void ExplicitCacheDirIsCreatedAndResolved()
    {
        var explicitDir = Path.Combine(_tmp.FullName, "explicit", "cache");

        var cacheDir = DiffCache.ResolveCacheDirectory(explicitDir, null);

        cacheDir.Should().Be(Path.GetFullPath(explicitDir));
        Directory.Exists(cacheDir).Should().BeTrue();
    }
}
