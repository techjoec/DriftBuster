using System.Text.Json;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Backend.Tests.MultiServer;

/// <summary>
/// Mirrors <c>tests/multi_server/test_multi_server.py</c>. The two module entrypoint tests exercised the stdin/stdout protocol,
/// which the in-process runner replaces: their assertions run against <see cref="MultiServerRunner.Run"/> and
/// <see cref="MultiServerSchema.ValidateResponse"/>, the version check the GUI facade applies to every response, instead (an
/// in-process request carries no schema version to reject). The
/// cache directory tests live in <see cref="MultiServerCacheDirectoryTests"/>, which runs apart because it sets
/// <c>DRIFTBUSTER_DATA_ROOT</c>.
/// </summary>
public sealed class MultiServerTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-multi-server-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string CacheDir => Path.Combine(_tmp.FullName, "cache");

    internal static MultiServerPlan SamplePlan(string host, int priority, bool isPreferred = false) => new()
    {
        HostId = host,
        Label = host,
        Roots = [RepoPaths.Fixtures("multi-server", host)],
        IsPreferred = isPreferred,
        Priority = priority,
    };

    [Fact]
    public void MultiServerGeneratesCatalogAndDrilldown()
    {
        var runner = new MultiServerRunner(CacheDir);
        MultiServerPlan[] plans = [SamplePlan("server01", 10, isPreferred: true), SamplePlan("server02", 5)];

        var response = runner.Run(plans, cancellationToken: TestContext.Current.CancellationToken);

        response.Version.Should().Be(MultiServerSchema.Version);
        response.Results.Should().HaveCount(2);
        response.Catalog.Should().NotBeEmpty("expected catalog entries");
        var appEntry = response.Catalog.First(entry => entry.DisplayName.EndsWith("appsettings.json", StringComparison.Ordinal));
        appEntry.PresentHosts.Should().BeEquivalentTo(["server01", "server02"]);
        appEntry.DriftCount.Should().BeGreaterThanOrEqualTo(1);

        var drilldown = response.Drilldown.First(entry => entry.DisplayName.EndsWith("appsettings.json", StringComparison.Ordinal));
        drilldown.BaselineHostId.Should().Be("server01");
        var servers = drilldown.Servers.ToDictionary(entry => entry.HostId, StringComparer.Ordinal);
        servers["server01"].IsBaseline.Should().BeTrue();
        servers["server02"].Status.Should().BeOneOf("Drift", "Match");
    }

    [Fact]
    public void MultiServerUsesCacheOnSubsequentRuns()
    {
        var runner = new MultiServerRunner(CacheDir);
        MultiServerPlan[] plans = [SamplePlan("server01", 1, isPreferred: true), SamplePlan("server02", 0)];

        var first = runner.Run(plans, cancellationToken: TestContext.Current.CancellationToken);
        first.Results.Should().Contain(result => !result.UsedCache, "expected cold run without cache");

        var second = runner.Run(plans, cancellationToken: TestContext.Current.CancellationToken);
        var cachedFlags = second.Results.Where(result => result.Availability == ServerAvailabilityStatus.Found).Select(result => result.UsedCache).ToList();
        cachedFlags.Should().NotBeEmpty();
        cachedFlags.Should().AllSatisfy(flag => flag.Should().BeTrue("expected hot run to reuse cache entries"));
    }

    [Fact]
    public void ConfigIdsAreDeterministic()
    {
        var runner = new MultiServerRunner(CacheDir);
        MultiServerPlan[] plans = [SamplePlan("server01", 5, isPreferred: true), SamplePlan("server02", 1), SamplePlan("server03", 0)];

        var first = runner.Run(plans, cancellationToken: TestContext.Current.CancellationToken);
        var second = runner.Run(plans, cancellationToken: TestContext.Current.CancellationToken);

        var firstIds = first.Catalog.Select(entry => entry.ConfigId).Order(StringComparer.Ordinal).ToList();
        var secondIds = second.Catalog.Select(entry => entry.ConfigId).Order(StringComparer.Ordinal).ToList();
        firstIds.Should().Equal(secondIds);
    }

    [Fact]
    public void MissingRootsMarkedNotFound()
    {
        var runner = new MultiServerRunner(CacheDir);
        var missingPlan = new MultiServerPlan { HostId = "missing", Label = "Missing host", Roots = ["/does/not/exist"] };

        var response = runner.Run([missingPlan], cancellationToken: TestContext.Current.CancellationToken);

        var result = response.Results[0];
        result.Availability.Should().Be(ServerAvailabilityStatus.NotFound);
        result.Status.Should().Be(ServerScanStatus.Failed);
        response.Catalog.Should().BeEmpty();
    }

    private sealed class FakeDetector : Detector
    {
        public override IReadOnlyList<(string Path, DetectionMatch? Match)> ScanPath(string root, string glob = "**/*", bool resetBudget = true)
            => throw new DetectorIOException(RepoPaths.Fixtures("multi-server", "server01", "appsettings.json"), "denied");
    }

    [Fact]
    public void DetectorPermissionErrorsAreReported()
    {
        var runner = new MultiServerRunner(CacheDir);
        var plan = SamplePlan("server01", 1);
        runner.Detector = new FakeDetector();

        var response = runner.Run([plan], cancellationToken: TestContext.Current.CancellationToken);

        var result = response.Results[0];
        result.Status.Should().Be(ServerScanStatus.Failed);
        result.Availability.Should().Be(ServerAvailabilityStatus.PermissionDenied);
        result.Message.Should().Contain("Permission denied");
    }

    [Fact]
    public void BuildCatalogHandlesOfflineAndPartialHosts()
    {
        var runner = new MultiServerRunner(CacheDir);
        MultiServerPlan[] plans = [SamplePlan("server01", 10, isPreferred: true), SamplePlan("server02", 5)];
        var originalScanPlan = runner.ScanPlan;
        runner.ScanPlan = (plan, existingRoots, secretHits, token) => string.Equals(plan.HostId, "server02", StringComparison.Ordinal)
            ? throw new InvalidOperationException("simulated offline host")
            : originalScanPlan(plan, existingRoots, secretHits, token);

        ServerScanResponse response;
        try
        {
            response = runner.Run(plans, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            runner.ScanPlan = originalScanPlan;
        }

        var offlineResult = response.Results.First(result => string.Equals(result.HostId, "server02", StringComparison.Ordinal));
        offlineResult.Availability.Should().Be(ServerAvailabilityStatus.Offline);
        offlineResult.Status.Should().Be(ServerScanStatus.Failed);

        response.Catalog.Should().NotBeEmpty("expected catalog entries");
        response.Catalog.Should().Contain(entry => entry.MissingHosts.Contains("server02"));
        response.Catalog.Should().Contain(entry => string.Equals(entry.CoverageStatus, "partial", StringComparison.Ordinal));

        response.Drilldown.Should().NotBeEmpty("expected drilldown entries");
        var offlineServer = response.Drilldown.SelectMany(entry => entry.Servers).First(server => string.Equals(server.HostId, "server02", StringComparison.Ordinal));
        offlineServer.Status.Should().Be("Offline");
    }

    [Fact]
    public void MultiServerReportsSamplingGuardrail()
    {
        var runner = new MultiServerRunner(CacheDir, sampleBudget: 256, sampleSize: 128);
        var plan = SamplePlan("server01", 1, isPreferred: true);

        var response = runner.Run([plan], cancellationToken: TestContext.Current.CancellationToken);

        var result = response.Results[0];
        result.SamplingGuardrailTriggered.Should().BeTrue();
        result.Message.Should().Contain("Sample budget reached");
    }

    [Fact]
    public void DrilldownIncludesSanitizedDiffSummary()
    {
        var runner = new MultiServerRunner(CacheDir);
        MultiServerPlan[] plans = [SamplePlan("server01", 10, isPreferred: true), SamplePlan("server02", 5)];

        var response = runner.Run(plans, cancellationToken: TestContext.Current.CancellationToken);

        var summaries = response.Drilldown.Where(entry => entry.DiffSummary is not null).Select(entry => entry.DiffSummary!.Value).ToList();
        summaries.Should().NotBeEmpty("expected sanitized diff summary payload");
        var summary = summaries[0];
        summary.GetProperty("comparison_count").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        var comparison = summary.GetProperty("comparisons")[0];
        comparison.GetProperty("summary").GetProperty("before_digest").GetString().Should().StartWith("sha256:");
    }

    [Fact]
    public void MultiServerModuleEntrypointRoundTrip()
    {
        var request = new[]
        {
            new ServerScanPlan
            {
                HostId = "server01",
                Label = "server01",
                Scope = ServerScanScope.CustomRoots,
                Roots = [Path.GetFullPath(RepoPaths.Fixtures("multi-server", "server01"))],
                Baseline = new ServerScanBaselinePreference { IsPreferred = true, Priority = 10 },
            },
            new ServerScanPlan
            {
                HostId = "server02",
                Label = "server02",
                Scope = ServerScanScope.CustomRoots,
                Roots = [Path.GetFullPath(RepoPaths.Fixtures("multi-server", "server02"))],
                Baseline = new ServerScanBaselinePreference { IsPreferred = false, Priority = 5 },
            },
        };
        var progress = new CollectingProgress();
        var runner = new MultiServerRunner(CacheDir);

        var payload = runner.Run(request.Select(MultiServerPlan.FromServerScanPlan), progress, TestContext.Current.CancellationToken);

        progress.Updates.Should().NotBeEmpty("expected progress updates from the runner");
        progress.Updates.Select(update => update.Status).Should().Contain(ServerScanStatus.Running);
        MultiServerSchema.ValidateResponse(payload);
        payload.Version.Should().Be(MultiServerSchema.Version);
        payload.Results.Should().HaveCount(2);
        payload.Catalog.Should().NotBeEmpty();

        var serialized = JsonSerializer.Serialize(payload);
        var roundTripped = JsonSerializer.Deserialize<ServerScanResponse>(serialized)!;
        roundTripped.Results.Should().HaveCount(2);
        roundTripped.Catalog.Should().HaveCount(payload.Catalog.Length);
    }

    [Fact]
    public void MultiServerModuleEntrypointReportsSchemaErrors()
    {
        var response = () => MultiServerSchema.ValidateResponse(new ServerScanResponse { Version = "multi-server.v0" });
        response.Should().Throw<InvalidOperationException>().WithMessage("*Unsupported multi-server schema version*");
    }

    [Fact]
    public void EmitProgressThrottlesDuplicateMessages()
    {
        var progress = new CollectingProgress();
        var throttle = new ProgressThrottle();

        throttle.Report(progress, "host-a", ServerScanStatus.Running, "Scanning", 1.0, DateTimeOffset.UtcNow);
        throttle.Report(progress, "host-a", ServerScanStatus.Running, "Scanning", 1.01, DateTimeOffset.UtcNow);

        progress.Updates.Should().HaveCount(1);
    }

    [Fact]
    public void EmitProgressEmitsWhenMessageChanges()
    {
        var progress = new CollectingProgress();
        var throttle = new ProgressThrottle();

        throttle.Report(progress, "host-a", ServerScanStatus.Running, "Scanning", 2.0, DateTimeOffset.UtcNow);
        throttle.Report(progress, "host-a", ServerScanStatus.Running, "Finishing", 2.01, DateTimeOffset.UtcNow);

        progress.Updates.Select(update => update.Message).Should().Equal("Scanning", "Finishing");
    }

    [Fact]
    public void EmitProgressEmitsAfterInterval()
    {
        var progress = new CollectingProgress();
        var throttle = new ProgressThrottle();

        throttle.Report(progress, "host-a", ServerScanStatus.Running, "Scanning", 5.0, DateTimeOffset.UtcNow);
        throttle.Report(progress, "host-a", ServerScanStatus.Running, "Scanning", 5.2, DateTimeOffset.UtcNow);

        progress.Updates.Should().HaveCount(2);
    }

    /// <summary>
    /// Python escaped non-ASCII on the stdout transport so console code pages could not break it. Progress now crosses no
    /// transport: the message reaches the consumer unchanged, and the model's JSON escapes it.
    /// </summary>
    [Fact]
    public void EmitProgressEscapesNonAsciiForConsoleSafety()
    {
        var progress = new CollectingProgress();
        var throttle = new ProgressThrottle();

        throttle.Report(progress, "host-a", ServerScanStatus.Running, "prefix﻿suffix", 9.0, DateTimeOffset.UtcNow);

        var update = progress.Updates.Single();
        update.Message.Should().Be("prefix﻿suffix");
        JsonSerializer.Serialize(update).Should().Contain("\\uFEFF");
    }
}
