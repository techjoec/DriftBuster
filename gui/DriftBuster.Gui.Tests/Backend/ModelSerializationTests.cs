using System.Text.Json;

using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.Tests.Backend;

public sealed class ModelSerializationTests
{
    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json)!;
    }

    [Fact]
    public void DiffResult_round_trips_with_defaults()
    {
        var original = new DiffResult();
        var result = RoundTrip(original);
        result.Versions.Should().BeEmpty();
        result.Comparisons.Should().BeEmpty();
    }

    [Fact]
    public void DiffResult_round_trips_with_values()
    {
        var original = new DiffResult
        {
            Versions = new[] { "v1.json", "v2.json" },
            Comparisons = new[]
            {
                new DiffComparison
                {
                    From = "v1.json",
                    To = "v2.json",
                    UnifiedDiff = "--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new",
                },
            },
        };

        var result = RoundTrip(original);
        result.Versions.Should().Equal("v1.json", "v2.json");
        result.Comparisons.Should().HaveCount(1);
        result.Comparisons[0].From.Should().Be("v1.json");
        result.Comparisons[0].UnifiedDiff.Should().Contain("+new");
    }

    [Fact]
    public void DiffResult_json_ignore_properties_do_not_round_trip()
    {
        var original = new DiffResult
        {
            RawJson = "raw-content",
            SanitizedJson = "sanitized-content",
        };

        var result = RoundTrip(original);
        result.RawJson.Should().BeEmpty();
        result.SanitizedJson.Should().BeEmpty();
    }

    [Fact]
    public void HuntResult_round_trips()
    {
        var original = new HuntResult
        {
            Directory = "/tmp/scan",
            Pattern = "*.config",
            Count = 2,
            Hits = new[]
            {
                new HuntHit
                {
                    Path = "/tmp/scan/web.config",
                    RelativePath = "web.config",
                    LineNumber = 10,
                    Excerpt = "connectionString=secret",
                    Rule = new HuntRuleSummary { Name = "connection-string", Description = "Database connection strings" },
                },
            },
        };

        var result = RoundTrip(original);
        result.Directory.Should().Be("/tmp/scan");
        result.Pattern.Should().Be("*.config");
        result.Count.Should().Be(2);
        result.Hits.Should().HaveCount(1);
        result.Hits[0].LineNumber.Should().Be(10);
        result.Hits[0].Rule.Name.Should().Be("connection-string");
    }

    [Fact]
    public void HuntResult_json_ignore_does_not_round_trip()
    {
        var original = new HuntResult { RawJson = "raw-hunt" };
        var result = RoundTrip(original);
        result.RawJson.Should().BeEmpty();
    }

    [Fact]
    public void ServerScanPlan_round_trips_with_nested_objects()
    {
        var original = new ServerScanPlan
        {
            HostId = "host-01",
            Label = "Production",
            Scope = ServerScanScope.CustomRoots,
            Roots = new[] { "/etc/app", "/var/lib/app" },
            ThrottleSeconds = 2.5,
            CachedAt = new DateTimeOffset(2026, 2, 14, 12, 0, 0, TimeSpan.Zero),
            Baseline = new ServerScanBaselinePreference { IsPreferred = true, Priority = 1, Role = "primary" },
            Export = new ServerScanExportOptions
            {
                IncludeCatalog = true,
                IncludeDrilldown = false,
                IncludeDiffs = true,
                IncludeSummary = true,
            },
        };

        var result = RoundTrip(original);
        result.HostId.Should().Be("host-01");
        result.Roots.Should().Equal("/etc/app", "/var/lib/app");
        result.ThrottleSeconds.Should().Be(2.5);
        result.CachedAt.Should().NotBeNull();
        result.Baseline.IsPreferred.Should().BeTrue();
        result.Baseline.Priority.Should().Be(1);
        result.Baseline.Role.Should().Be("primary");
        result.Export.IncludeDrilldown.Should().BeFalse();
    }

    [Fact]
    public void RunProfileDefinition_round_trips_with_options_dict()
    {
        var original = new RunProfileDefinition
        {
            Name = "nightly-scan",
            Description = "Full nightly configuration scan",
            Sources = new[] { "/etc/app", "/opt/service" },
            Baseline = "/etc/app",
            Options = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sample_size"] = "256000",
                ["content_type"] = "auto",
            },
            SecretScanner = new SecretScannerOptions
            {
                IgnoreRules = new[] { "example-token" },
                IgnorePatterns = new[] { "*.test" },
            },
        };

        var result = RoundTrip(original);
        result.Name.Should().Be("nightly-scan");
        result.Description.Should().Be("Full nightly configuration scan");
        result.Sources.Should().Equal("/etc/app", "/opt/service");
        result.Baseline.Should().Be("/etc/app");
        result.Options.Should().ContainKey("sample_size");
        result.Options["content_type"].Should().Be("auto");
        result.SecretScanner.IgnoreRules.Should().Equal("example-token");
        result.SecretScanner.IgnorePatterns.Should().Equal("*.test");
    }

    [Fact]
    public void ScheduleDefinition_round_trips_with_window_and_metadata()
    {
        var original = new ScheduleDefinition
        {
            Name = "daily-check",
            Profile = "nightly-scan",
            Every = "1d",
            StartAt = "02:00",
            Window = new ScheduleWindowDefinition
            {
                Start = "01:00",
                End = "05:00",
                Timezone = "UTC",
            },
            Tags = new[] { "production", "critical" },
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["team"] = "infra" },
        };

        var result = RoundTrip(original);
        result.Name.Should().Be("daily-check");
        result.Every.Should().Be("1d");
        result.StartAt.Should().Be("02:00");
        result.Window.Should().NotBeNull();
        result.Window!.Start.Should().Be("01:00");
        result.Window.End.Should().Be("05:00");
        result.Window.Timezone.Should().Be("UTC");
        result.Tags.Should().Equal("production", "critical");
        result.Metadata.Should().ContainKey("team");
    }

    [Fact]
    public void SecretScannerOptions_defaults_to_empty_arrays()
    {
        var original = new SecretScannerOptions();
        var result = RoundTrip(original);
        result.IgnoreRules.Should().BeEmpty();
        result.IgnorePatterns.Should().BeEmpty();
    }

    [Fact]
    public void ServerScanResponse_round_trips_with_defaults()
    {
        var original = new ServerScanResponse();
        var result = RoundTrip(original);
        result.Version.Should().BeEmpty();
        result.Results.Should().BeEmpty();
        result.Catalog.Should().BeEmpty();
        result.Drilldown.Should().BeEmpty();
        result.Summary.Should().BeNull();
    }

    [Fact]
    public void DiffChangeSummary_round_trips_line_counts()
    {
        var original = new DiffChangeSummary
        {
            BeforeDigest = "abc123",
            AfterDigest = "def456",
            DiffDigest = "diff789",
            BeforeLines = 100,
            AfterLines = 110,
            AddedLines = 15,
            RemovedLines = 5,
            ChangedLines = 10,
        };

        var result = RoundTrip(original);
        result.BeforeDigest.Should().Be("abc123");
        result.AfterDigest.Should().Be("def456");
        result.DiffDigest.Should().Be("diff789");
        result.BeforeLines.Should().Be(100);
        result.AfterLines.Should().Be(110);
        result.AddedLines.Should().Be(15);
        result.RemovedLines.Should().Be(5);
        result.ChangedLines.Should().Be(10);
    }

    [Fact]
    public void ScanProgress_round_trips()
    {
        var ts = new DateTimeOffset(2026, 2, 14, 12, 30, 0, TimeSpan.Zero);
        var original = new ScanProgress
        {
            HostId = "srv-01",
            Status = ServerScanStatus.Running,
            Message = "Scanning /etc",
            Timestamp = ts,
        };

        var result = RoundTrip(original);
        result.HostId.Should().Be("srv-01");
        result.Message.Should().Be("Scanning /etc");
        result.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void ServerScanExportOptions_defaults_all_true()
    {
        var original = new ServerScanExportOptions();
        var result = RoundTrip(original);
        result.IncludeCatalog.Should().BeTrue();
        result.IncludeDrilldown.Should().BeTrue();
        result.IncludeDiffs.Should().BeTrue();
        result.IncludeSummary.Should().BeTrue();
    }

    [Fact]
    public void ServerScanBaselinePreference_defaults()
    {
        var original = new ServerScanBaselinePreference();
        var result = RoundTrip(original);
        result.IsPreferred.Should().BeFalse();
        result.Priority.Should().Be(0);
        result.Role.Should().Be("auto");
    }
}
