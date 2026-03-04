using System.Reflection;
using System.Text.Json;
using System.Threading;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class ServerSelectionViewModelAdditionalTests
{
    private static ServerScanResponse BuildResponse(string baselineId, IReadOnlyList<ServerScanPlan> plans)
    {
        var labels = plans.Select(plan => string.IsNullOrWhiteSpace(plan.Label) ? plan.HostId : plan.Label).ToArray();
        return new ServerScanResponse
        {
            Results = plans.Select(plan => new ServerScanResult
            {
                HostId = plan.HostId,
                Label = plan.Label,
                Status = ServerScanStatus.Succeeded,
                Message = "Completed",
                Timestamp = DateTimeOffset.UtcNow,
                Availability = ServerAvailabilityStatus.Found,
            }).ToArray(),
            Catalog = new[]
            {
                new ConfigCatalogEntry
                {
                    ConfigId = "plugins",
                    DisplayName = "plugins.conf",
                    Format = "ini",
                    DriftCount = 1,
                    Severity = "medium",
                    PresentHosts = labels.Take(labels.Length - 1).ToArray(),
                    MissingHosts = labels.Length > 1 ? new[] { labels[^1] } : Array.Empty<string>(),
                    CoverageStatus = labels.Length > 1 ? "partial" : "full",
                    HasMaskedTokens = true,
                    LastUpdated = DateTimeOffset.UtcNow,
                },
            },
            Drilldown = new[]
            {
                new ConfigDrilldown
                {
                    ConfigId = "plugins",
                    DisplayName = "plugins.conf",
                    Format = "ini",
                    DriftCount = 1,
                    BaselineHostId = baselineId,
                    DiffBefore = "before",
                    DiffAfter = "after",
                    UnifiedDiff = string.Empty,
                    Notes = new[] { "Investigate drift" },
                    Servers = plans.Select((plan, index) => new ConfigServerDetail
                    {
                        HostId = plan.HostId,
                        Label = plan.Label,
                        Present = true,
                        IsBaseline = index == 0,
                        Status = index == 0 ? "Baseline" : "Drift",
                        DriftLineCount = index,
                        RedactionStatus = index % 2 == 0 ? "Masked" : "Visible",
                        Masked = index % 2 == 0,
                        HasSecrets = index % 2 == 1,
                        LastSeen = DateTimeOffset.UtcNow,
                    }).ToArray(),
                },
            },
        };
    }

    [Fact]
    public async Task Handles_root_management_and_session_persistence()
    {
        var cache = new InMemorySessionCacheService();

        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = CreateScanResponseWithProgress,
        };

        var toast = new ToastService(action => action());
        var viewModel = new ServerSelectionViewModel(service, toast, cache);

        var server = viewModel.Servers[0];
        ConfigureCustomRootFlow(viewModel, server);

        await viewModel.RunAllCommand.ExecuteAsync(null);
        viewModel.CatalogViewModel.HasEntries.Should().BeTrue();
        viewModel.FilteredActivityEntries.Should().NotBeEmpty();
        viewModel.IsBusy.Should().BeFalse();
        server.IsEnabled.Should().BeTrue();
        AssertDrilldownContainsHost(viewModel, server.HostId);

        await VerifyStandalonePersistenceFlowAsync(cache, toast);
    }

    private static void ConfigureCustomRootFlow(ServerSelectionViewModel viewModel, ServerSlotViewModel server)
    {
        server.Scope = ServerScanScope.CustomRoots;
        var absoluteRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(absoluteRoot);
        server.NewRootPath = absoluteRoot;
        viewModel.AddRootCommand.Execute(server);
        server.Roots.Should().Contain(root => string.Equals(root.Path, absoluteRoot, StringComparison.OrdinalIgnoreCase));
        var defaultRoot = server.Roots.First(root => !string.Equals(root.Path, absoluteRoot, StringComparison.OrdinalIgnoreCase));
        viewModel.RemoveRootCommand.Execute(defaultRoot);
        server.Roots.Should().ContainSingle(root => string.Equals(root.Path, absoluteRoot, StringComparison.OrdinalIgnoreCase));

        server.NewRootPath = "relative";
        viewModel.AddRootCommand.Execute(server);
        var lastRoot = server.Roots.Last();
        lastRoot.ValidationState.Should().Be(RootValidationState.Invalid);
        lastRoot.StatusMessage.Should().Contain("absolute");
        viewModel.RemoveRootCommand.Execute(lastRoot);
        server.Roots.Should().ContainSingle();
    }

    private static async Task VerifyStandalonePersistenceFlowAsync(InMemorySessionCacheService cache, ToastService toast)
    {
        var persistenceViewModel = new ServerSelectionViewModel(new FakeDriftbusterService(), toast, cache);
        persistenceViewModel.PersistSessionState = true;
        persistenceViewModel.Servers[0].Label = "Persist me";
        persistenceViewModel.SaveSessionCommand.CanExecute(null).Should().BeTrue();
        await persistenceViewModel.SaveSessionCommand.ExecuteAsync(null).ConfigureAwait(false);
        cache.Snapshot.Should().NotBeNull("SaveSessionCommand should have saved snapshot");
        cache.Snapshot!.Servers.Should().Contain(entry => entry.Label == "Persist me");

        persistenceViewModel.ActivityFilter = ActivityFilterOption.Errors;
        persistenceViewModel.ActivityFilter = ActivityFilterOption.All;

        persistenceViewModel.ClearHistoryCommand.Execute(null);
        cache.Cleared.Should().BeTrue();
        persistenceViewModel.HasActiveServers.Should().BeTrue();
        persistenceViewModel.ShowDrilldownForHostCommand.CanExecute(persistenceViewModel.Servers[0].HostId).Should().BeFalse();
    }

    [Fact]
    public async Task SaveSession_after_run_persists_snapshot_on_same_viewmodel()
    {
        var cache = new InMemorySessionCacheService
        {
            Snapshot = new ServerSelectionCache
            {
                PersistSession = true,
            },
        };
        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = CreateScanResponseWithProgress,
        };
        var toast = new ToastService(action => action());
        var viewModel = new ServerSelectionViewModel(service, toast, cache);

        SpinWait.SpinUntil(() => viewModel.PersistSessionState, TimeSpan.FromSeconds(5)).Should().BeTrue();
        await viewModel.RunAllCommand.ExecuteAsync(null);
        viewModel.PersistSessionState.Should().BeTrue();
        var saveMethod = typeof(ServerSelectionViewModel).GetMethod("SaveSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        saveMethod.Should().NotBeNull();
        var saveTask = (Task?)saveMethod!.Invoke(viewModel, null);
        saveTask.Should().NotBeNull();
        await saveTask!;
        viewModel.StatusBanner.Should().Be("Session saved.");

        cache.Snapshot.Should().NotBeNull();
        cache.Snapshot!.Servers.Should().Contain(entry => string.Equals(entry.HostId, viewModel.Servers[0].HostId, StringComparison.Ordinal));
    }

    [Fact]
    public void Reorders_servers_and_updates_indexes()
    {
        var service = new FakeDriftbusterService();
        var toast = new ToastService(action => action());
        var viewModel = new ServerSelectionViewModel(service, toast, new InMemorySessionCacheService());

        var originalOrder = viewModel.Servers.Select(slot => slot.HostId).ToList();
        originalOrder.Should().HaveCountGreaterThan(2);

        var source = viewModel.Servers[2];
        var target = viewModel.Servers[0];

        viewModel.ReorderServer(source.HostId, target.HostId, insertBefore: true);

        viewModel.Servers[0].HostId.Should().Be(source.HostId);
        viewModel.Servers[0].Index.Should().Be(0);
        viewModel.Servers[1].HostId.Should().Be(target.HostId);

        // Dropping the original target after the moved host keeps the order stable.
        viewModel.ReorderServer(target.HostId, source.HostId, insertBefore: false);
        viewModel.Servers[1].HostId.Should().Be(target.HostId);

        // Invalid reorder requests are ignored.
        viewModel.ReorderServer(source.HostId, source.HostId, insertBefore: true);
        viewModel.ReorderServer("missing", target.HostId, insertBefore: true);

        var resultingOrder = viewModel.Servers.Select(slot => slot.HostId).ToList();
        resultingOrder.Should().Contain(source.HostId);
    }

    [Fact]
    public void CanAcceptReorder_rejects_busy_or_identical_requests()
    {
        var service = new FakeDriftbusterService();
        var toast = new ToastService(action => action());
        var viewModel = new ServerSelectionViewModel(service, toast, new InMemorySessionCacheService());

        var target = viewModel.Servers[0];
        var source = viewModel.Servers[1];

        viewModel.IsBusy = true;
        viewModel.CanAcceptReorder(source.HostId, target).Should().BeFalse();

        viewModel.IsBusy = false;
        viewModel.CanAcceptReorder(target.HostId, target).Should().BeFalse();
        viewModel.CanAcceptReorder(source.HostId.ToUpperInvariant(), source).Should().BeFalse();

        viewModel.CanAcceptReorder(source.HostId, target).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanAcceptReorder_rejects_missing_source_ids(string? sourceHostId)
    {
        var service = new FakeDriftbusterService();
        var toast = new ToastService(action => action());
        var viewModel = new ServerSelectionViewModel(service, toast, new InMemorySessionCacheService());

        var target = viewModel.Servers[0];

        viewModel.IsBusy.Should().BeFalse();
        viewModel.CanAcceptReorder(sourceHostId, target).Should().BeFalse();
    }

    [Fact]
    public void ReorderServer_ignores_requests_when_busy()
    {
        var service = new FakeDriftbusterService();
        var toast = new ToastService(action => action());
        var viewModel = new ServerSelectionViewModel(service, toast, new InMemorySessionCacheService());

        var source = viewModel.Servers[1];
        var target = viewModel.Servers[0];
        var before = viewModel.Servers.Select(slot => slot.HostId).ToList();

        viewModel.IsBusy = true;
        viewModel.ReorderServer(source.HostId, target.HostId, insertBefore: true);

        viewModel.Servers.Select(slot => slot.HostId).Should().Equal(before);
    }

    [Fact]
    public async Task Emits_attention_toast_and_activity_for_unavailable_hosts()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var logDirectory = Path.Combine(repositoryRoot, "artifacts", "logs", "gui-validation");
        var originalLogSnapshot = SnapshotLogDirectory(logDirectory);

        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = BuildMixedAvailabilityResponse,
        };

        var toast = new ToastService(action => action());
        var viewModel = new ServerSelectionViewModel(service, toast, new InMemorySessionCacheService());

        await viewModel.RunAllCommand.ExecuteAsync(null);

        var warningToasts = toast.ActiveToasts
            .Where(notification => notification.Level == ToastLevel.Warning)
            .ToList();
        warningToasts.Should().ContainSingle();
        var warning = warningToasts.Single();
        warning.Title.Should().Be("Hosts require attention");
        warning.Message.Should().Contain("Supporting App: Offline");
        warning.Message.Should().Contain("FreakyFriday: PermissionDenied");

        viewModel.ActivityEntries.Should().Contain(entry =>
            entry.Summary == "Hosts require attention" &&
            entry.Severity == ActivitySeverity.Warning &&
            entry.Detail.Contains("Supporting App: Offline"));

        using var evidenceDirectory = new TempDirectory("server-selection-attention-toast");
        try
        {
            await WriteAndVerifyEvidenceAsync(evidenceDirectory.Path, warning, viewModel);
        }
        finally
        {
            var currentLogSnapshot = SnapshotLogDirectory(logDirectory);
            currentLogSnapshot.Should().BeEquivalentTo(originalLogSnapshot);
        }
    }

    [Fact]
    public async Task RunAll_surfaces_error_state_when_scan_throws()
    {
        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = (_, _, _) => Task.FromException<ServerScanResponse>(new InvalidOperationException("network down")),
        };
        var toast = new ToastService(action => action());
        var viewModel = new ServerSelectionViewModel(service, toast, new InMemorySessionCacheService());

        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.IsBusy.Should().BeFalse();
        viewModel.StatusBanner.Should().Be("Scan failed: network down");
        viewModel.ActivityEntries.Should().Contain(entry =>
            entry.Severity == ActivitySeverity.Error &&
            entry.Summary == "Scan failed" &&
            entry.Detail.Contains("network down", StringComparison.Ordinal));
        toast.ActiveToasts.Should().Contain(notification =>
            notification.Level == ToastLevel.Error &&
            notification.Title == "Multi-server scan failed" &&
            notification.Message.Contains("network down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Provides_deterministic_drilldown_gating_and_telemetry()
    {
        var logPath = Path.Combine("artifacts", "logs", "drilldown-ready.json");
        if (File.Exists(logPath))
        {
            File.Delete(logPath);
        }

        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = (plans, _, _) =>
            {
                var list = plans.ToList();
                return Task.FromResult(BuildResponse(list[0].HostId, list));
            },
        };

        var toast = new ToastService(action => action());
        var viewModel = new ServerSelectionViewModel(service, toast, new InMemorySessionCacheService());

        var server = viewModel.Servers[0];

        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.ShowDrilldownForHostCommand.CanExecute(server.HostId).Should().BeTrue();
        viewModel.ShowDrilldownForHostCommand.CanExecute("missing").Should().BeFalse();

        viewModel.IsBusy = true;
        viewModel.ShowDrilldownForHostCommand.CanExecute(server.HostId).Should().BeFalse();

        viewModel.IsBusy = false;
        viewModel.ShowDrilldownForHostCommand.CanExecute(server.HostId).Should().BeTrue();

        server.IsEnabled = false;
        viewModel.ShowDrilldownForHostCommand.CanExecute(server.HostId).Should().BeFalse();

        server.IsEnabled = true;
        viewModel.ShowDrilldownForHostCommand.CanExecute(server.HostId).Should().BeTrue();

        viewModel.ShowDrilldownForHostCommand.Execute(server.HostId);

        File.Exists(logPath).Should().BeTrue();
        using var stream = File.OpenRead(logPath);
        using var document = JsonDocument.Parse(stream);

        var root = document.RootElement;
        root.GetProperty("eventName").GetString().Should().Be("DrilldownTelemetry");
        var state = root.GetProperty("state");
        state.GetProperty("stage").GetString().Should().Be("drilldown-opened");
        state.GetProperty("drilldownCount").GetInt32().Should().BeGreaterThan(0);

        var hosts = state.GetProperty("hosts");
        hosts.GetArrayLength().Should().BeGreaterThan(0);
        hosts.EnumerateArray().Any(host =>
                string.Equals(host.GetProperty("hostId").GetString(), server.HostId, StringComparison.OrdinalIgnoreCase) &&
                host.GetProperty("hasDrilldown").GetBoolean())
            .Should().BeTrue();
    }

    [Fact]
    public void Virtualization_flags_follow_performance_profile()
    {
        var service = new FakeDriftbusterService();
        var toast = new ToastService(action => action());

        var conservativeProfile = new PerformanceProfile(virtualizationThreshold: 10);
        var conservativeViewModel = new ServerSelectionViewModel(service, toast, performanceProfile: conservativeProfile);
        conservativeViewModel.UseVirtualizedServerList.Should().BeFalse();

        var profile = new PerformanceProfile(virtualizationThreshold: 2);
        var viewModel = new ServerSelectionViewModel(service, toast, performanceProfile: profile);

        viewModel.UseVirtualizedActivityFeed.Should().BeFalse();
        viewModel.UseVirtualizedServerList.Should().BeTrue();

        var logActivity = typeof(ServerSelectionViewModel)
            .GetMethod("LogActivity", BindingFlags.Instance | BindingFlags.NonPublic);
        logActivity.Should().NotBeNull();

        logActivity!.Invoke(viewModel, new object?[] { ActivitySeverity.Info, "one", null, ActivityCategory.General });
        viewModel.UseVirtualizedActivityFeed.Should().BeFalse();

        logActivity.Invoke(viewModel, new object?[] { ActivitySeverity.Info, "two", null, ActivityCategory.General });
        viewModel.UseVirtualizedActivityFeed.Should().BeTrue();
    }

    private static async Task WriteAndVerifyEvidenceAsync(
        string evidencePath, ToastNotification warning, ServerSelectionViewModel viewModel)
    {
        var payload = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            warningToast = new
            {
                warning.Title,
                warning.Message,
            },
            activities = viewModel.ActivityEntries
                .Where(entry => string.Equals(entry.Summary, "Hosts require attention", StringComparison.Ordinal))
                .Select(entry => new
                {
                    entry.Summary,
                    entry.Detail,
                    Severity = entry.Severity.ToString(),
                })
                .ToArray(),
        };

        var logPath = Path.Combine(evidencePath, "server-selection-attention-toast.json");
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        // Persist evidence in a disposable temp directory so repository logs remain untouched.
        await File.WriteAllTextAsync(logPath, json + Environment.NewLine).ConfigureAwait(false);

        var savedEvidence = await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
        savedEvidence.Should().Contain("Hosts require attention");
    }

    private static Task<ServerScanResponse> CreateScanResponseWithProgress(
        IEnumerable<ServerScanPlan> plans, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var list = plans.ToList();
        foreach (var plan in list)
        {
            progress?.Report(new ScanProgress
            {
                HostId = plan.HostId,
                Status = ServerScanStatus.Running,
                Message = "Scanning",
                Timestamp = DateTimeOffset.UtcNow,
            });
        }

        return Task.FromResult(BuildResponse(list[0].HostId, list));
    }

    private static void AssertDrilldownContainsHost(ServerSelectionViewModel viewModel, string hostId)
    {
        var response = (ServerScanResponse?)typeof(ServerSelectionViewModel)
            .GetField("_lastResponse", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(viewModel);
        response.Should().NotBeNull();
        response!.Drilldown.Should().NotBeNull();
        response.Drilldown.Should().NotBeEmpty();
        var drilldownHosts = response.Drilldown!
            .SelectMany(entry => entry.Servers ?? Array.Empty<ConfigServerDetail>())
            .Select(detail => detail.HostId)
            .ToList();
        drilldownHosts.Should().Contain(hostId);
        var drilldownDetail = response.Drilldown!
            .SelectMany(entry => entry.Servers ?? Array.Empty<ConfigServerDetail>())
            .FirstOrDefault(detail => string.Equals(detail.HostId, hostId, StringComparison.OrdinalIgnoreCase));
        drilldownDetail.Should().NotBeNull();
        drilldownDetail!.Present.Should().BeTrue();
        viewModel.ShowDrilldownForHostCommand.CanExecute(hostId).Should().BeTrue();
    }

    private static Task<ServerScanResponse> BuildMixedAvailabilityResponse(
        IEnumerable<ServerScanPlan> plans, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var list = plans.ToList();
        foreach (var plan in list)
        {
            progress?.Report(new ScanProgress
            {
                HostId = plan.HostId,
                Status = ServerScanStatus.Running,
                Message = "Scanning",
                Timestamp = DateTimeOffset.UtcNow,
            });
        }

        var results = list.Select((plan, index) => new ServerScanResult
        {
            HostId = plan.HostId,
            Label = plan.Label,
            Status = index switch
            {
                0 => ServerScanStatus.Succeeded,
                1 => ServerScanStatus.Failed,
                _ => ServerScanStatus.Failed,
            },
            Message = index switch
            {
                0 => "Completed",
                1 => "Host offline",
                _ => "Permission denied",
            },
            Timestamp = DateTimeOffset.UtcNow,
            Availability = index switch
            {
                0 => ServerAvailabilityStatus.Found,
                1 => ServerAvailabilityStatus.Offline,
                _ => ServerAvailabilityStatus.PermissionDenied,
            },
        }).ToArray();

        return Task.FromResult(new ServerScanResponse
        {
            Results = results,
        });
    }

    private static IReadOnlyDictionary<string, LogFileSnapshot> SnapshotLogDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new Dictionary<string, LogFileSnapshot>(StringComparer.Ordinal);
        }

        return Directory
            .GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => path,
                path => new LogFileSnapshot(File.GetLastWriteTimeUtc(path), File.ReadAllText(path)),
                StringComparer.Ordinal);
    }

    private readonly record struct LogFileSnapshot(DateTime LastWriteUtc, string Content);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string? hint = null)
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DriftBuster.Gui.Tests");
            Directory.CreateDirectory(root);
            var folderName = string.IsNullOrWhiteSpace(hint)
                ? Guid.NewGuid().ToString("N")
                : $"{hint}-{Guid.NewGuid():N}";
            Path = System.IO.Path.Combine(root, folderName);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
