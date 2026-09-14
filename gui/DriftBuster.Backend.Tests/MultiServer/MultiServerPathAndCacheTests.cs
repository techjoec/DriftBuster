using System.Globalization;

using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Backend.Tests.MultiServer;

/// <summary>
/// Runner behaviour around paths and the cache that the parity cases cannot reach or that needs a unit-level pin: a root with
/// ".." after a symlink, the fix a suffix counting the plan's roots, files too long to read whole, and cache failures that fail the
/// host offline with Python's message.
/// </summary>
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

    // The algorithm-r1 dotdot-through-symlink parity case: fixed Python evaluates real/app, not the link's lexical parent.
    [Fact]
    public void ARootWithDotDotAfterASymlinkScansTheLinkTargetsParent()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "'..' after a symlink resolves physically on POSIX kernels");
        Write("tree/real/app/app.json", "{\"Server\": \"real.corp.local\", \"host\": \"a\"}\n");
        Write("tree/real/app/nested/deep.ini", "[deep]\nserver = deep.corp.local host\n");
        Write("tree/other/app.json", "{\"Server\": \"other.corp.local\", \"host\": \"b\"}\n");
        Write("tree/lexical.json", "{\"Lexical\": \"sibling of the link, never under the real parent\"}\n");
        var tree = Path.Combine(_tmp.FullName, "tree");
        Directory.CreateSymbolicLink(Path.Combine(tree, "shortcut"), "real/app/nested");
        var root = $"{tree}/shortcut/..";

        var response = new MultiServerRunner(CacheDir).Run(
            [Plan("physical", root) with { IsPreferred = true }, Plan("other", Path.Combine(tree, "other"))],
            cancellationToken: TestContext.Current.CancellationToken);

        response.Results[0].Message.Should().Be("Evaluated 2 configuration(s).");
        response.Results[0].Roots.Should().Equal(root);
        response.Catalog.Select(entry => entry.ConfigId).Should().Equal("json/generic/app-json", "toml/generic/nested/deep-ini");
        response.Catalog[0].PresentHosts.Should().Equal("physical", "other");
        response.Catalog.Should().Contain(entry => entry.HasSecrets);
        response.Drilldown[0].DiffBefore.Should().Contain("real.corp.local");
        MultiServerRunner.RootFingerprint([root]).Should().Be(MultiServerPlan.Sha1Hex(Path.Combine(tree, "real", "app")));
    }

    [Fact]
    public void TheRootSuffixCountsThePlansRootsMissingOnesIncluded()
    {
        Write("r1/app/settings.json", "{\"a\": 1}\n");
        Write("r2/app/settings.json", "{\"a\": 2}\n");
        var plan = Plan("host", Path.Combine(_tmp.FullName, "missing"), Path.Combine(_tmp.FullName, "r1"), "  ", Path.Combine(_tmp.FullName, "r2"));

        var response = new MultiServerRunner(CacheDir).Run([MultiServerPlan.FromServerScanPlan(new ServerScanPlan { HostId = plan.HostId, Label = plan.Label, Roots = plan.Roots.ToArray() })], cancellationToken: TestContext.Current.CancellationToken);

        response.Results[0].Roots.Should().Equal(Path.Combine(_tmp.FullName, "r1"), Path.Combine(_tmp.FullName, "r2"));
        response.Catalog.Select(entry => entry.ConfigId).Should().Equal("json/generic/app/settings-json", "json/generic/app/settings-json@root2");
    }

    [Fact]
    public void TheSameRootTwiceTakesTheSecondPosition()
    {
        var root = Path.GetDirectoryName(Write("r/app.json", "{\"a\": 1}\n"))!;

        var response = new MultiServerRunner(CacheDir).Run([Plan("host", root, Path.Combine(_tmp.FullName, "missing"), root)], cancellationToken: TestContext.Current.CancellationToken);

        response.Catalog.Select(entry => entry.ConfigId).Should().Equal("json/generic/app-json", "json/generic/app-json@root2");
    }

    [Fact]
    public void AFileLongerThanTheReadLimitIsSkippedAndTheHostSucceeds()
    {
        Write("host/big.json", "{\"a\": \"" + new string('x', 64) + "\"}\n");
        Write("host/small.json", "{\"a\": 1}\n");
        var runner = new MultiServerRunner(CacheDir) { MaxTextBytes = 32 };
        PlanScan? scan = null;
        var original = runner.ScanPlan;
        runner.ScanPlan = (plan, roots, secrets, token) => scan = original(plan, roots, secrets, token);

        var response = runner.Run([Plan("host", Path.Combine(_tmp.FullName, "host"))], cancellationToken: TestContext.Current.CancellationToken);

        response.Results[0].Status.Should().Be(ServerScanStatus.Succeeded);
        response.Results[0].Message.Should().Be("Evaluated 1 configuration(s).");
        response.Catalog.Should().ContainSingle().Which.ConfigId.Should().Be("json/generic/small-json");
        scan!.SkippedFiles.Select(Path.GetFileName).Should().Equal("big.json");
    }

    [Fact]
    public void ACacheEntryThatIsNotAnObjectFailsTheHostOffline()
    {
        var root = Path.GetDirectoryName(Write("host/app.json", "{\"a\": 1}\n"))!;
        var runner = new MultiServerRunner(CacheDir);
        runner.Run([Plan("host", root)], cancellationToken: TestContext.Current.CancellationToken);
        File.WriteAllText(runner.Cache.EntryPath("host", "json/generic/app-json"), "[1, 2]");

        var response = runner.Run([Plan("host", root)], cancellationToken: TestContext.Current.CancellationToken);

        response.Results[0].Status.Should().Be(ServerScanStatus.Failed);
        response.Results[0].Availability.Should().Be(ServerAvailabilityStatus.Offline);
        response.Results[0].Message.Should().Be("Scan failed: 'list' object has no attribute 'get'");
        response.Catalog.Should().BeEmpty();
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
        response.Results[0].Message.Should().Be(MultiServerRunner.TruncateCodePoints($"Scan failed: [Errno 13] Permission denied: '{entry}'", 160));
        Directory.GetFileSystemEntries(CacheDir).Should().BeEmpty();
    }

    // A second run over a changed file makes the first run's entry stale, so the runner saves over it.
    private (MultiServerRunner Runner, string File, string Entry, string Before) ScanOnceThenChange()
    {
        var file = Write("host/app.json", "{\"a\": 1}\n");
        var runner = new MultiServerRunner(CacheDir);
        runner.Run([Plan("host", Path.GetDirectoryName(file)!)], cancellationToken: TestContext.Current.CancellationToken);
        var entry = runner.Cache.EntryPath("host", "json/generic/app-json");
        var before = File.ReadAllText(entry);
        File.WriteAllText(file, "{\"a\": 2}\n");
        return (runner, file, entry, before);
    }

    // CPython 3.13 on the same steps: write_text truncates the stale entry in place, which a directory that refuses new names allows.
    [Fact]
    public void AStaleWritableEntryInACacheDirectoryThatRefusesNewFilesIsRewrittenInPlace()
    {
        if (!CanDenyAccess)
        {
            return;
        }

        var (runner, file, entry, before) = ScanOnceThenChange();
        File.SetUnixFileMode(CacheDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var response = runner.Run([Plan("host", Path.GetDirectoryName(file)!)], cancellationToken: TestContext.Current.CancellationToken);

        response.Results[0].Status.Should().Be(ServerScanStatus.Succeeded);
        response.Results[0].Message.Should().Be("Evaluated 1 configuration(s).");
        File.ReadAllText(entry).Should().NotBe(before);
        Directory.GetFileSystemEntries(CacheDir).Should().Equal(entry);
    }

    // CPython 3.13 on the same steps: open(path, "w") on the read-only stale entry raises EACCES and the host goes offline.
    [Fact]
    public void AReadOnlyStaleEntryFailsTheHostOfflineAsOpeningItForWritingDoes()
    {
        if (!CanDenyAccess)
        {
            return;
        }

        var (runner, file, entry, before) = ScanOnceThenChange();
        File.SetUnixFileMode(entry, UnixFileMode.UserRead);

        var response = runner.Run([Plan("host", Path.GetDirectoryName(file)!)], cancellationToken: TestContext.Current.CancellationToken);

        response.Results[0].Availability.Should().Be(ServerAvailabilityStatus.Offline);
        response.Results[0].Message.Should().Be(MultiServerRunner.TruncateCodePoints($"Scan failed: [Errno 13] Permission denied: '{entry}'", 160));
        File.ReadAllText(entry).Should().Be(before);
        Directory.GetFileSystemEntries(CacheDir).Should().Equal(entry);
    }

    [Fact]
    public void ARootWhoseNameIsTooLongIsPermissionDenied()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "ENAMETOOLONG from statx is a Linux case");
        var root = Path.Combine(_tmp.FullName, new string('n', 300));

        var response = new MultiServerRunner(CacheDir).Run([Plan("host", root)], cancellationToken: TestContext.Current.CancellationToken);

        response.Results[0].Availability.Should().Be(ServerAvailabilityStatus.PermissionDenied);
        response.Results[0].Message.Should().Be(string.Create(CultureInfo.InvariantCulture, $"Permission denied: {root}"));
    }
}
