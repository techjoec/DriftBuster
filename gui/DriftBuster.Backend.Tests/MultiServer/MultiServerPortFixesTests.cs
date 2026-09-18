using System.Globalization;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Backend.Tests.MultiServer;

/// <summary>
/// Multi-server config ids from the relative path, unreadable files skipped and unreadable roots denied, atomic cache writes under
/// cancellation, per-run progress throttling and the severity thresholds.
/// </summary>
public sealed class MultiServerPortFixesTests : IDisposable
{
    private const string WebConfig = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <appSettings>\n    <add key=\"Mode\" value=\"{0}\" />\n  </appSettings>\n</configuration>\n";

    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-multi-server-fixes-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string CacheDir => Path.Combine(_tmp.FullName, "cache");

    [System.Runtime.Versioning.SupportedOSPlatformGuard("linux")]
    private static bool CanDenyAccess => OperatingSystem.IsLinux() && !string.Equals(Environment.UserName, "root", StringComparison.Ordinal);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_tmp.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static MultiServerPlan Plan(string host, params string[] roots) => new() { HostId = host, Label = host, Roots = roots };

    [Fact]
    public void TwoAppsWithWebConfigGetDistinctConfigIds()
    {
        Write("host/app1/web.config", string.Format(CultureInfo.InvariantCulture, WebConfig, "One"));
        Write("host/app2/web.config", string.Format(CultureInfo.InvariantCulture, WebConfig, "Two"));
        var runner = new MultiServerRunner(CacheDir);

        var response = runner.Run([Plan("host", Path.Combine(_tmp.FullName, "host"))], cancellationToken: TestContext.Current.CancellationToken);

        response.Results[0].Message.Should().Be("Evaluated 2 configuration(s).");
        var ids = response.Catalog.Select(entry => entry.ConfigId).ToList();
        ids.Should().Equal("structured-config-xml/web-config/app1/web-config", "structured-config-xml/web-config/app2/web-config");
        response.Catalog.Select(entry => entry.DisplayName).Should().Equal("app1/web.config", "app2/web.config");
    }

    [Fact]
    public void SameRelativePathUnderTwoRootsKeepsBothRecords()
    {
        Write("r0/app/web.config", string.Format(CultureInfo.InvariantCulture, WebConfig, "Zero"));
        Write("r1/app/web.config", string.Format(CultureInfo.InvariantCulture, WebConfig, "One"));
        var runner = new MultiServerRunner(CacheDir);
        var plan = Plan("host", Path.Combine(_tmp.FullName, "r0"), Path.Combine(_tmp.FullName, "r1"));

        var response = runner.Run([plan], cancellationToken: TestContext.Current.CancellationToken);

        response.Results[0].Message.Should().Be("Evaluated 2 configuration(s).");
        response.Catalog.Select(entry => entry.ConfigId).Should().Equal(
            "structured-config-xml/web-config/app/web-config",
            "structured-config-xml/web-config/app/web-config@root1");
        response.Drilldown[1].DiffAfter.Should().Contain("One");
    }

    [Fact]
    public void HostSucceedsWhenOneFileUnreadable()
    {
        if (!CanDenyAccess)
        {
            return;
        }

        Write("host/a-open.json", "{\"server\": \"open.corp.local\"}\n");
        var locked = Write("host/b-locked.json", "{\"server\": \"locked.corp.local\"}\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var runner = new MultiServerRunner(CacheDir);
            PlanScan? scan = null;
            var original = runner.ScanPlan;
            runner.ScanPlan = (plan, roots, token) => scan = original(plan, roots, token);

            var response = runner.Run([Plan("host", Path.Combine(_tmp.FullName, "host"))], cancellationToken: TestContext.Current.CancellationToken);

            var result = response.Results[0];
            result.Status.Should().Be(ServerScanStatus.Succeeded);
            result.Availability.Should().Be(ServerAvailabilityStatus.Found);
            result.Message.Should().Be("Evaluated 1 configuration(s).");
            response.Catalog.Should().ContainSingle().Which.DisplayName.Should().Be("a-open.json");
            scan!.SkippedFiles.Should().Equal(locked);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void UnreadableRootIsPermissionDenied()
    {
        if (!CanDenyAccess)
        {
            return;
        }

        var root = Path.Combine(_tmp.FullName, "locked-root");
        Write("locked-root/app.json", "{\"a\": 1}\n");
        File.SetUnixFileMode(root, UnixFileMode.None);
        try
        {
            var runner = new MultiServerRunner(CacheDir);

            var response = runner.Run([Plan("host", root)], cancellationToken: TestContext.Current.CancellationToken);

            var result = response.Results[0];
            result.Status.Should().Be(ServerScanStatus.Failed);
            result.Availability.Should().Be(ServerAvailabilityStatus.PermissionDenied);
            result.Message.Should().Be($"Permission denied: {root}");
            response.Catalog.Should().BeEmpty();
        }
        finally
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void CancellationLeavesNoPartialCacheFile()
    {
        using var first = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new MultiServerRunner(CacheDir);
        runner.Cache.TemporaryWritten = _ => first.Cancel();
        var plans = new[] { MultiServerTests.SamplePlan("server01", 1, isPreferred: true) };

        var cancelled = () => runner.Run(plans, cancellationToken: first.Token);

        cancelled.Should().Throw<OperationCanceledException>();
        Directory.EnumerateFileSystemEntries(CacheDir).Should().BeEmpty();

        using var later = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var writes = 0;
        runner.Cache.TemporaryWritten = _ =>
        {
            if (++writes == 3)
            {
                later.Cancel();
            }
        };
        var cancelledLater = () => runner.Run(plans, cancellationToken: later.Token);

        cancelledLater.Should().Throw<OperationCanceledException>();
        var entries = Directory.EnumerateFileSystemEntries(CacheDir).ToList();
        entries.Should().HaveCount(2).And.AllSatisfy(entry => entry.Should().EndWith(".json"));
        foreach (var entry in entries)
        {
            EngineJson.TryLoads(File.ReadAllText(entry), out var parsed).Should().BeTrue();
            parsed.Should().BeOfType<OrderedDictionary<string, object?>>().Which.Should().ContainKey("signature");
        }
    }

    [Fact]
    public void CancellationIsNotReportedAsAnOfflineHost()
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new MultiServerRunner(CacheDir);
        var original = runner.ScanPlan;
        runner.ScanPlan = (plan, roots, token) =>
        {
            source.Cancel();
            return original(plan, roots, token);
        };
        var progress = new CollectingProgress();

        var run = () => runner.Run([MultiServerTests.SamplePlan("server01", 1)], progress, source.Token);

        run.Should().Throw<OperationCanceledException>();
        progress.Updates.Select(update => update.Status).Should().Equal(ServerScanStatus.Running);
    }

    [Fact]
    public void ThrottleDelayIsCancellable()
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new MultiServerRunner(CacheDir);
        var plan = MultiServerTests.SamplePlan("server01", 1) with { ThrottleSeconds = 3600 };
        runner.ScanPlan = (_, _, _) =>
        {
            source.CancelAfter(TimeSpan.FromMilliseconds(50));
            return new PlanScan(new OrderedDictionary<string, ConfigRecord>(StringComparer.Ordinal), false, false, []);
        };

        var started = System.Diagnostics.Stopwatch.StartNew();
        var run = () => runner.Run([plan], cancellationToken: source.Token);

        run.Should().Throw<OperationCanceledException>();
        started.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void ProgressThrottleIsPerRun()
    {
        var runner = new MultiServerRunner(CacheDir) { Monotonic = static () => 1.0 };
        var progress = new CollectingProgress();
        var plan = Plan("missing", "/does/not/exist");

        runner.Run([plan], progress, TestContext.Current.CancellationToken);
        runner.Run([plan], progress, TestContext.Current.CancellationToken);

        progress.Updates.Select(update => (update.Status, update.Message)).Should().Equal(
            (ServerScanStatus.Running, "Scanning missing"),
            (ServerScanStatus.Failed, "No accessible roots."),
            (ServerScanStatus.Running, "Scanning missing"),
            (ServerScanStatus.Failed, "No accessible roots."));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 2)]
    [InlineData(8, 4)]
    public void SeverityThresholdsAcrossHostCounts(int hostCount, int highThreshold)
    {
        for (var drifting = 0; drifting < hostCount; drifting++)
        {
            var plans = Enumerable.Range(0, hostCount).Select(index => Plan($"h{index}")).ToList();
            var hostConfigs = new OrderedDictionary<string, OrderedDictionary<string, ConfigRecord>>(StringComparer.Ordinal);
            var availability = new Dictionary<string, ServerAvailabilityStatus>(StringComparer.Ordinal);
            foreach (var (plan, index) in plans.Select((plan, index) => (plan, index)))
            {
                var content = index >= 1 && index <= drifting ? "value = drift\n" : "value = same\n";
                hostConfigs[plan.HostId] = new OrderedDictionary<string, ConfigRecord>(StringComparer.Ordinal) { ["cfg"] = Record(plan.HostId, content) };
                availability[plan.HostId] = ServerAvailabilityStatus.Found;
            }

            var (catalog, drilldown) = CatalogBuilder.Build(plans, hostConfigs, availability, "h0", static () => DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

            var expected = drifting >= highThreshold ? "high" : drifting > 0 ? "medium" : "none";
            catalog.Single().DriftCount.Should().Be(drifting);
            catalog.Single().Severity.Should().Be(expected, "{0} of {1} hosts drift", drifting, hostCount);
            drilldown.Single().Servers.Count(server => string.Equals(server.Status, "Drift", StringComparison.Ordinal)).Should().Be(drifting);
            CatalogBuilder.Severity(drifting, hostCount).Should().Be(expected);
        }
    }

    private static ConfigRecord Record(string host, string content) => new()
    {
        ConfigId = "cfg",
        DisplayName = "app.conf",
        FormatId = "unix-conf",
        ContentType = "text",
        Canonical = content,
        Raw = content,
        FileHash = host,
        SourcePath = $"/{host}/app.conf",
        PluginName = "conf",
        RelativePath = "app.conf",
    };
}
