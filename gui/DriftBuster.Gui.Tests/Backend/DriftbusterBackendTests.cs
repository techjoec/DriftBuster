using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using DriftBuster.Backend;
using DriftBuster.Backend.Models;
using Xunit;

namespace DriftBuster.Gui.Tests.Backend;

[Collection("BackendTests")]
public sealed class DriftbusterBackendTests
{
    private readonly DriftbusterBackend _backend = new();
    private readonly BackendDataRootFixture _fixture;

    public DriftbusterBackendTests(BackendDataRootFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PingAsync_returns_pong()
    {
        var response = await _backend.PingAsync(TestContext.Current.CancellationToken);
        Assert.Equal("pong", response);
    }

    [Fact]
    public async Task DiffAsync_builds_comparisons_and_serializes_raw_json()
    {
        var baseline = Path.GetTempFileName();
        var comparisonPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(baseline, "alpha");
            File.WriteAllText(comparisonPath, "beta");

            var result = await _backend.DiffAsync(new[] { baseline, comparisonPath }, TestContext.Current.CancellationToken);

            Assert.Single(result.Comparisons);
            Assert.Contains("alpha", result.Comparisons[0].Plan.Before, StringComparison.Ordinal);
            Assert.Contains("beta", result.Comparisons[0].Plan.After, StringComparison.Ordinal);
            result.Comparisons[0].UnifiedDiff.Should().Contain("---");
            Assert.False(string.IsNullOrWhiteSpace(result.RawJson));
            Assert.False(string.IsNullOrWhiteSpace(result.SanitizedJson));
            result.Summary.Should().NotBeNull();

            using var document = JsonDocument.Parse(result.SanitizedJson);
            var root = document.RootElement;
            root.GetProperty("comparison_count").GetInt32().Should().Be(1);
            var comparisonElement = root.GetProperty("comparisons")[0];
            comparisonElement.GetProperty("summary").GetProperty("before_digest").GetString()
                .Should().StartWith("sha256:");
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(comparisonPath);
        }
    }

    [Fact]
    public async Task DiffAsync_supports_multiple_comparison_paths()
    {
        var baseline = Path.GetTempFileName();
        var comparisonA = Path.GetTempFileName();
        var comparisonB = Path.GetTempFileName();

        try
        {
            File.WriteAllText(baseline, "alpha\n");
            File.WriteAllText(comparisonA, "alpha\nbeta\n");
            File.WriteAllText(comparisonB, "alpha\ngamma\n");

            var result = await _backend.DiffAsync(new[] { baseline, comparisonA, comparisonB }, TestContext.Current.CancellationToken);

            result.Comparisons.Should().HaveCount(2);
            result.Comparisons[0].Metadata.LeftPath.Should().Be(baseline);
            result.Comparisons[1].Metadata.LeftPath.Should().Be(baseline);
            result.Comparisons.Select(comparison => comparison.To).Should().Contain(new[]
            {
                Path.GetFileName(comparisonA),
                Path.GetFileName(comparisonB),
            });

            using var document = JsonDocument.Parse(result.SanitizedJson);
            document.RootElement.GetProperty("comparison_count").GetInt32().Should().Be(2);
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(comparisonA);
            File.Delete(comparisonB);
        }
    }

    [Fact]
    public async Task DiffAsync_chooses_content_type_from_detection_not_extension()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var xmlBaseline = Path.Combine(directory.FullName, "settings.txt");
        var xmlCandidate = Path.Combine(directory.FullName, "settings-new.txt");
        var jsonBaseline = Path.Combine(directory.FullName, "appsettings.json");
        var jsonCandidate = Path.Combine(directory.FullName, "appsettings.Production.json");
        var plainConfig = Path.Combine(directory.FullName, "plain.csproj");
        File.WriteAllText(xmlBaseline, "<?xml version=\"1.0\"?>\n<configuration>\n  <add b=\"2\" a=\"1\" />\n</configuration>\n");
        File.WriteAllText(xmlCandidate, "<?xml version=\"1.0\"?>\n<configuration>\n  <add a=\"1\" b=\"3\" />\n</configuration>\n");
        File.WriteAllText(jsonBaseline, "{\n  \"b\": 1,\n  \"a\": 2\n}\n");
        File.WriteAllText(jsonCandidate, "{\n  \"a\": 2,\n  \"b\": 1\n}\n");
        File.WriteAllText(plainConfig, "alpha = 1\n");

        try
        {
            var xml = await _backend.DiffAsync(new[] { xmlBaseline, xmlCandidate }, TestContext.Current.CancellationToken);
            xml.Comparisons[0].Plan.ContentType.Should().Be("xml");
            xml.Comparisons[0].Plan.Before.Should().Be("<?xml version=\"1.0\"?>\n<configuration><add a=\"1\" b=\"2\" /></configuration>");

            var json = await _backend.DiffAsync(new[] { jsonBaseline, jsonCandidate }, TestContext.Current.CancellationToken);
            json.Comparisons[0].Plan.ContentType.Should().Be("text");
            json.Comparisons[0].Metadata.ContentType.Should().Be("text");

            var config = await _backend.DiffAsync(new[] { plainConfig, plainConfig }, TestContext.Current.CancellationToken);
            config.Comparisons[0].Plan.ContentType.Should().Be("text");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DiffAsync_throws_for_missing_comparisons()
    {
        var baseline = Path.GetTempFileName();
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.DiffAsync(new[] { baseline }, TestContext.Current.CancellationToken));
            Assert.Contains("Provide at least two file paths", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public async Task HuntAsync_returns_hits_and_filters_by_pattern()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var file = Path.Combine(directory.FullName, "config.txt");
        File.WriteAllText(file, "server: backend-host.example.com\nversion: 1.2.3");

        try
        {
            var result = await _backend.HuntAsync(directory.FullName, pattern: null, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEmpty(result.Hits);
            Assert.Contains(result.Hits, hit => hit.RelativePath.EndsWith("config.txt", StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(result.RawJson));

            var filtered = await _backend.HuntAsync(directory.FullName, pattern: "nomatch", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, filtered.Count);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HuntAsync_runs_the_seven_ported_rules_in_walk_order_with_plan_transforms()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(directory.FullName, "a"));
        File.WriteAllText(Path.Combine(directory.FullName, "b.config"), "<add name=\"Db\" connectionString=\"Server=sql.corp.local;\" />\n");
        File.WriteAllText(Path.Combine(directory.FullName, "a", "install.txt"), "install path C:\\Program Files\\Vendor\n");
        File.WriteAllText(Path.Combine(directory.FullName, "a.txt"), "<feature name=\"x\" enabled=\"true\"/>\n");

        try
        {
            var result = await _backend.HuntAsync(directory.FullName, pattern: null, cancellationToken: TestContext.Current.CancellationToken);

            result.Hits.Select(hit => hit.RelativePath).Should().Equal("a/install.txt", "a.txt", "b.config");
            result.Hits.Select(hit => hit.Rule.Name).Should().Equal("install-path", "feature-flag", "connection-string");
            var install = result.Hits[0];
            install.Excerpt.Should().Be("install path C:\\Program Files\\Vendor");
            install.Metadata!.PlanTransform!.Value.Should().Be("C:\\Program Files");
            install.Metadata.PlanTransform.Placeholder.Should().Be("{{ install_path }}");
            install.Rule.Patterns.Should().Equal(@"[A-Za-z]:\\[\w\-\.\s]+", @"/opt/[\w\-\.]+");
            result.UnreadableFiles.Should().BeNull();
            result.RawJson.Should().Contain("\"plan_transform\"").And.NotContain("unreadable_files");

            var filtered = await _backend.HuntAsync(directory.FullName, pattern: "  FEATURE ", cancellationToken: TestContext.Current.CancellationToken);
            filtered.Pattern.Should().Be("FEATURE");
            filtered.Hits.Select(hit => hit.Rule.Name).Should().Equal("feature-flag");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HuntAsync_skips_unreadable_files_and_reports_them()
    {
        if (OperatingSystem.IsWindows() || string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
        {
            return;
        }

        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var locked = Path.Combine(directory.FullName, "locked.txt");
        File.WriteAllText(locked, "server host: locked.corp.local\n");
        File.WriteAllText(Path.Combine(directory.FullName, "open.txt"), "server host: open.corp.local\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            var result = await _backend.HuntAsync(directory.FullName, pattern: null, cancellationToken: TestContext.Current.CancellationToken);

            result.Hits.Select(hit => hit.RelativePath).Should().Equal("open.txt");
            result.UnreadableFiles.Should().Equal(locked);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            directory.Delete(recursive: true);
        }
    }

    // No timing race: the tree holds 4000 links to one 128 KiB file, which every hunt rule searches in full (no hits), so an
    // uncancelled hunt runs for many seconds, far past the 100 ms after which the token is cancelled. HuntAsync forwards its
    // token into HuntEngine, which polls it before every file and inside every pattern search; the hunt ends with
    // OperationCanceledException within the test timeout only if that token is honoured.
    [Fact(Timeout = 60_000)]
    public async Task HuntAsync_honours_cancellation_while_hunting_a_large_tree()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var source = Path.Combine(directory.FullName, "source.txt");
            File.WriteAllText(source, string.Concat(Enumerable.Repeat("key=\"flag\" v ", 128 * 1024 / 13)));
            var tree = Directory.CreateDirectory(Path.Combine(directory.FullName, "tree"));
            for (var index = 0; index < 4000; index++)
            {
                File.CreateSymbolicLink(Path.Combine(tree.FullName, $"f{index:D4}.config"), source);
            }

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            var hunt = () => _backend.HuntAsync(tree.FullName, pattern: null, cancellationToken: cancellation.Token);

            await hunt.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // A FIFO passed to the planner is refused from its file type; opening it for reading would block until a writer appeared.
    [Fact(Timeout = 30_000)]
    public async Task DiffAsync_refuses_a_fifo_without_opening_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "FIFOs are a Linux case");
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var baseline = Path.Combine(directory.FullName, "baseline.txt");
            var fifo = Path.Combine(directory.FullName, "pipe.txt");
            File.WriteAllText(baseline, "a\n");
            using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { fifo } })!)
            {
                await mkfifo.WaitForExitAsync(TestContext.Current.CancellationToken);
            }

            var diff = () => _backend.DiffAsync(new[] { baseline, fifo }, TestContext.Current.CancellationToken);

            (await diff.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Comparison path is not a regular file: *");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // GUI-only behaviour: the diff planner decodes a UTF-16 or UTF-32 file by its byte order mark, where multi-server reads the
    // bytes as UTF-8 with replacement.
    [Fact]
    public async Task DiffAsync_decodes_files_by_their_byte_order_mark()
    {
        var baseline = Path.GetTempFileName();
        var comparison = Path.GetTempFileName();
        try
        {
            File.WriteAllText(baseline, "key = alpha\n", new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            File.WriteAllText(comparison, "key = beta\n", new System.Text.UTF32Encoding(bigEndian: false, byteOrderMark: true));

            var result = await _backend.DiffAsync(new[] { baseline, comparison }, TestContext.Current.CancellationToken);

            result.Comparisons[0].Plan.Before.Should().Be("key = alpha\n");
            result.Comparisons[0].Plan.After.Should().Be("key = beta\n");
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(comparison);
        }
    }

    [Fact]
    public async Task HuntAsync_throws_for_missing_path()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => _backend.HuntAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), null, TestContext.Current.CancellationToken));
        Assert.Contains("Path does not exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareMultiServerCacheDirectory_uses_data_root_cache_and_migrates_legacy_files()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "DriftbusterRepo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        try
        {
            var legacyCache = Path.Combine(repositoryRoot, "artifacts", "cache", "diffs");
            Directory.CreateDirectory(legacyCache);
            File.WriteAllText(Path.Combine(legacyCache, "sample.json"), "{}");

            var method = typeof(DriftbusterBackend).GetMethod("PrepareMultiServerCacheDirectory", BindingFlags.NonPublic | BindingFlags.Static);
            var cacheDirectory = method!.Invoke(null, new object?[] { repositoryRoot }) as string;

            cacheDirectory.Should().NotBeNullOrWhiteSpace();
            cacheDirectory!.StartsWith(_fixture.Root, StringComparison.OrdinalIgnoreCase).Should().BeTrue();
            cacheDirectory.Should().Contain(Path.Combine("cache", "diffs"));
            File.Exists(Path.Combine(cacheDirectory, "sample.json")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(repositoryRoot))
            {
                Directory.Delete(repositoryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunServerScansAsync_writes_the_diff_cache_under_the_data_root()
    {
        var plans = new[]
        {
            new ServerScanPlan
            {
                HostId = "alpha",
                Label = "Primary",
                Scope = ServerScanScope.CustomRoots,
                Roots = new[] { Path.Combine(RepositoryRoot(), "fixtures", "multi-server", "server01") },
            },
        };

        var response = await _backend.RunServerScansAsync(plans, progress: null, TestContext.Current.CancellationToken);

        response.Results.Should().ContainSingle().Which.Status.Should().Be(ServerScanStatus.Succeeded);
        var cacheDirectory = Path.Combine(_fixture.Root, "cache", "diffs");
        Directory.Exists(cacheDirectory).Should().BeTrue();
        Directory.GetFiles(cacheDirectory, "*.json").Should().NotBeEmpty();
    }

    [Fact]
    public async Task RunServerScansAsync_reports_queued_then_runner_progress_and_honours_cancellation()
    {
        var plans = new[]
        {
            new ServerScanPlan
            {
                HostId = "alpha",
                Label = "Primary",
                Scope = ServerScanScope.CustomRoots,
                Roots = new[] { Path.Combine(RepositoryRoot(), "fixtures", "multi-server", "server01") },
            },
        };
        var updates = new List<ScanProgress>();

        await _backend.RunServerScansAsync(plans, new InlineProgress(updates), TestContext.Current.CancellationToken);

        updates.Select(update => update.Status).Should().Equal(ServerScanStatus.Queued, ServerScanStatus.Running, ServerScanStatus.Succeeded);
        updates[1].Message.Should().Be("Scanning Primary");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var cancel = () => _backend.RunServerScansAsync(plans, progress: null, cancelled.Token);
        await cancel.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunProfile_round_trip_saves_and_lists_profiles()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "DriftbusterTests", Guid.NewGuid().ToString("N"));
        var sourceDir = Directory.CreateDirectory(Path.Combine(baseDir, "sources"));
        var baselineFile = Path.Combine(sourceDir.FullName, "baseline.txt");
        var dataFile = Path.Combine(sourceDir.FullName, "data.txt");
        File.WriteAllText(baselineFile, "baseline");
        File.WriteAllText(dataFile, "data");

        try
        {
            var profile = new RunProfileDefinition
            {
                Name = "Profile One",
                Baseline = baselineFile,
                Sources = new[] { new RunProfileSource(baselineFile), new RunProfileSource(Path.Combine(sourceDir.FullName, "*.txt")) },
                Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["key"] = "value" },
            };

            var result = await _backend.RunProfileAsync(profile, saveProfile: true, baseDir: baseDir, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(Directory.Exists(result.OutputDir));
            Assert.True(result.Files.Length >= 2);
            Assert.NotNull(result.Profile);

            var listed = await _backend.ListProfilesAsync(baseDir, TestContext.Current.CancellationToken);
            Assert.Contains(listed.Profiles, p => string.Equals(p.Name, "Profile One", StringComparison.Ordinal));

            var savedProfilePath = Path.Combine(baseDir, "Profiles", "Profile-One", "profile.json");
            Assert.True(File.Exists(savedProfilePath));
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveProfileAsync_requires_name()
    {
        var profile = new RunProfileDefinition { Name = "" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.SaveProfileAsync(profile, baseDir: Path.GetTempPath(), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListProfilesAsync_loads_profiles_and_raises_on_invalid_json()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "DriftbusterTests", Guid.NewGuid().ToString("N"));
        var profilesRoot = Path.Combine(baseDir, "Profiles");
        Directory.CreateDirectory(profilesRoot);

        var validDir = Directory.CreateDirectory(Path.Combine(profilesRoot, "Valid"));

        var profileDefinition = new RunProfileDefinition
        {
            Name = "Valid Profile",
            Sources = new[] { new RunProfileSource("config.json"), new RunProfileSource("logs") { Alias = "logs", Optional = true } },
        };

        var json = JsonSerializer.Serialize(profileDefinition, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(validDir.FullName, "profile.json"), json);

        try
        {
            var result = await _backend.ListProfilesAsync(baseDir, TestContext.Current.CancellationToken);

            Assert.Single(result.Profiles);
            Assert.Equal("Valid Profile", result.Profiles[0].Name);
            Assert.Equal("logs", result.Profiles[0].Sources[1].Alias);
            Assert.True(result.Profiles[0].Sources[1].Optional);

            // Listing reads every profile.json and raises on the first that is not JSON.
            var invalidDir = Directory.CreateDirectory(Path.Combine(profilesRoot, "Broken"));
            File.WriteAllText(Path.Combine(invalidDir.FullName, "profile.json"), "{ invalid json");
            await Assert.ThrowsAnyAsync<ArgumentException>(() => _backend.ListProfilesAsync(baseDir, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunServerScansAsync_executes_multi_server_runner()
    {
        var backend = new DriftbusterBackend();
        var sampleRoot = Path.Combine(RepositoryRoot(), "fixtures", "multi-server");

        var plans = new[]
        {
            new ServerScanPlan
            {
                HostId = "baseline",
                Label = "server01",
                Scope = ServerScanScope.CustomRoots,
                Roots = new[] { Path.Combine(sampleRoot, "server01") },
                Baseline = new ServerScanBaselinePreference { IsPreferred = true, Priority = 10, Role = "auto" },
                Export = new ServerScanExportOptions(),
            },
            new ServerScanPlan
            {
                HostId = "drift",
                Label = "server02",
                Scope = ServerScanScope.CustomRoots,
                Roots = new[] { Path.Combine(sampleRoot, "server02") },
                Baseline = new ServerScanBaselinePreference { IsPreferred = false, Priority = 5, Role = "auto" },
                Export = new ServerScanExportOptions(),
            },
        };

        var response = await backend.RunServerScansAsync(plans, progress: null, CancellationToken.None);

        Assert.Equal("multi-server.v1", response.Version);
        Assert.Equal(2, response.Results.Length);
        Assert.Contains(response.Results, result => string.Equals(result.HostId, "baseline", StringComparison.Ordinal) && result.Status == ServerScanStatus.Succeeded && result.Availability == ServerAvailabilityStatus.Found);
        Assert.Contains(response.Results, result => string.Equals(result.HostId, "drift", StringComparison.Ordinal) && result.Status == ServerScanStatus.Succeeded && result.Availability == ServerAvailabilityStatus.Found);
        Assert.NotEmpty(response.Catalog);
        var appEntry = response.Catalog.First(entry => entry.DisplayName.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, appEntry.PresentHosts.Length);
        Assert.NotEmpty(response.Drilldown);
    }

    // The scan runs in process, so relative roots resolve against the test host's working directory; fixtures are addressed from
    // the repository root instead.
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DriftBuster.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Unable to locate the repository root from the test host.");
    }

    private sealed class InlineProgress(List<ScanProgress> updates) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => updates.Add(value);
    }
}
