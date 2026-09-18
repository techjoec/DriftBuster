using System.Globalization;

using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;
using DriftBuster.Backend.Tests.Secrets;

namespace DriftBuster.Backend.Tests.MultiServer;

/// <summary>
/// Runner behaviour for files too long to read whole and for a cache failure that fails the host offline.
/// </summary>
// Scans flag secrets through the process-wide secret rule cache that the secret scanner tests replace.
[Collection(SecretRuleCacheCollection.Name)]
public sealed class MultiServerPathAndCacheTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-multi-server-paths-");

    public void Dispose()
    {
        foreach (var directory in _tmp.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint) && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        _tmp.Delete(recursive: true);
    }

    private string CacheDir => Path.Combine(_tmp.FullName, "cache");

    [System.Runtime.Versioning.SupportedOSPlatformGuard("linux")]
    private static bool CanDenyAccess => OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess;

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_tmp.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static MultiServerPlan Plan(string host, params string[] roots) => new() { HostId = host, Label = host, Roots = roots };

    [Fact]
    public void AFileLongerThanTheReadLimitIsSkippedAndTheHostSucceeds()
    {
        Write("host/big.json", "{\"a\": \"" + new string('x', 64) + "\"}\n");
        Write("host/small.json", "{\"a\": 1}\n");
        var runner = new MultiServerRunner(CacheDir) { MaxTextBytes = 32 };
        PlanScan? scan = null;
        var original = runner.ScanPlan;
        runner.ScanPlan = (plan, roots, token) => scan = original(plan, roots, token);

        var response = runner.Run([Plan("host", Path.Combine(_tmp.FullName, "host"))], cancellationToken: TestContext.Current.CancellationToken);

        response.Results[0].Status.Should().Be(ServerScanStatus.Succeeded);
        response.Results[0].Message.Should().Be("Evaluated 1 configuration(s).");
        response.Catalog.Should().ContainSingle().Which.ConfigId.Should().Be("json/generic/small-json");
        scan!.SkippedFiles.Select(Path.GetFileName).Should().Equal("big.json");
    }

    [Fact]
    public void AnUnwritableCacheFailsTheHostOfflineNamingTheEntry()
    {
        if (!CanDenyAccess)
        {
            // Write permission is observable only for a non-root Linux user.
            return;
        }

        var root = Path.GetDirectoryName(Write("host/app.json", "{\"a\": 1}\n"))!;
        var runner = new MultiServerRunner(CacheDir);
        File.SetUnixFileMode(CacheDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var response = runner.Run([Plan("host", root)], cancellationToken: TestContext.Current.CancellationToken);

        var entry = runner.Cache.EntryPath("host", "json/generic/app-json");
        response.Results[0].Availability.Should().Be(ServerAvailabilityStatus.Offline);
        response.Results[0].Message.Should().Be(MultiServerRunner.TruncateCodePoints($"Scan failed: Access to the path '{entry}' is denied.", 160));
        Directory.GetFileSystemEntries(CacheDir).Should().BeEmpty();
    }
}
