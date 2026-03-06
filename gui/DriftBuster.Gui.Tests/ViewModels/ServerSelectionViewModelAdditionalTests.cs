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

    [Fact]
    public void AddServerCommand_appends_enabled_hosts_with_stable_ids()
    {
        var viewModel = new ServerSelectionViewModel(new FakeDriftbusterService(), new ToastService(action => action()), new InMemorySessionCacheService());
        viewModel.Servers.Should().HaveCount(6);

        viewModel.AddServerCommand.Execute(null);
        viewModel.Servers.Should().HaveCount(7);
        viewModel.Servers[6].HostId.Should().Be("host-07");
        viewModel.Servers[6].Label.Should().Be("Host 07");
        viewModel.Servers[6].IsEnabled.Should().BeTrue();

        viewModel.AddServerCommand.Execute(null);
        viewModel.Servers.Should().HaveCount(8);
        viewModel.Servers[7].HostId.Should().Be("host-08");
        viewModel.Servers[7].Label.Should().Be("Host 08");
    }

    [Fact]
    public void LoadSession_restores_hosts_not_in_default_seed()
    {
        var cache = new InMemorySessionCacheService
        {
            Snapshot = new ServerSelectionCache
            {
                PersistSession = true,
                Servers = new List<ServerSelectionCacheEntry>
                {
                    new()
                    {
                        HostId = "host-01",
                        Label = "App Inc",
                        Enabled = true,
                        Scope = ServerScanScope.AllDrives,
                        Roots = Array.Empty<string>(),
                    },
                    new()
                    {
                        HostId = "host-07",
                        Label = "Remote API",
                        Enabled = true,
                        Scope = ServerScanScope.CustomRoots,
                        Roots = new[] { "C:\\Program Files" },
                    },
                },
            },
        };

        var viewModel = new ServerSelectionViewModel(new FakeDriftbusterService(), new ToastService(action => action()), cache);

        SpinWait.SpinUntil(() => viewModel.Servers.Any(server => string.Equals(server.HostId, "host-07", StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(2))
            .Should().BeTrue();
        var restored = viewModel.Servers.Single(server => string.Equals(server.HostId, "host-07", StringComparison.OrdinalIgnoreCase));
        restored.Label.Should().Be("Remote API");
        restored.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void AddRoot_uses_path_leaf_for_default_label()
    {
        var viewModel = new ServerSelectionViewModel(new FakeDriftbusterService(), new ToastService(action => action()), new InMemorySessionCacheService());
        var slot = viewModel.Servers[0];
        slot.Label.Should().Be("App Inc");
        slot.Scope = ServerScanScope.CustomRoots;

        var autoNameRoot = Path.Combine(Path.GetTempPath(), $"Server1.company.hell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(autoNameRoot);

        slot.NewRootPath = autoNameRoot;
        viewModel.AddRootCommand.Execute(slot);

        slot.Label.Should().Be(Path.GetFileName(autoNameRoot));
    }

    [Fact]
    public void AddRoot_does_not_override_manual_label()
    {
        var viewModel = new ServerSelectionViewModel(new FakeDriftbusterService(), new ToastService(action => action()), new InMemorySessionCacheService());
        var slot = viewModel.Servers[0];
        slot.Scope = ServerScanScope.CustomRoots;
        slot.Label = "Manual Server Name";

        var customRoot = Path.Combine(Path.GetTempPath(), $"Server2.company.hell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(customRoot);

        slot.NewRootPath = customRoot;
        viewModel.AddRootCommand.Execute(slot);

        slot.Label.Should().Be("Manual Server Name");
    }

    [Theory]
    [InlineData(@"C:\configs\Server1.company.hell", "Server1.company.hell")]
    [InlineData(@"/mnt/configs/server-a", "server-a")]
    [InlineData(@"\\share\configs\server-z\", "server-z")]
    public void TryDeriveHostLabelFromRootPath_extracts_leaf(string path, string expected)
    {
        ServerSelectionViewModel.TryDeriveHostLabelFromRootPath(path).Should().Be(expected);
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
        viewModel.SaveSessionCommand.CanExecute(null).Should().BeTrue();
        await viewModel.SaveSessionCommand.ExecuteAsync(null);
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
    public async Task RunAll_with_single_host_explains_no_comparison()
    {
        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = (plans, _, _) =>
            {
                var planList = plans.ToList();
                return Task.FromResult(new ServerScanResponse
                {
                    Results = planList.Select(plan => new ServerScanResult
                    {
                        HostId = plan.HostId,
                        Label = plan.Label,
                        Status = ServerScanStatus.Succeeded,
                        Message = "Completed",
                        Timestamp = DateTimeOffset.UtcNow,
                        Availability = ServerAvailabilityStatus.Found,
                    }).ToArray(),
                    Summary = new ServerScanSummary
                    {
                        BaselineHostId = planList[0].HostId,
                        TotalHosts = 1,
                        ConfigsEvaluated = 20,
                        DriftingConfigs = 0,
                        GeneratedAt = DateTimeOffset.UtcNow,
                    },
                });
            },
        };

        var toast = new ToastService(action => action());
        var viewModel = new ServerSelectionViewModel(service, toast, new InMemorySessionCacheService());
        for (var index = 1; index < viewModel.Servers.Count; index++)
        {
            viewModel.Servers[index].IsEnabled = false;
        }

        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.StatusBanner.Should().Contain("Add at least one more host");
        toast.ActiveToasts.Should().Contain(notification =>
            notification.Level == ToastLevel.Success &&
            notification.Title == "Multi-server scan complete" &&
            notification.Message.Contains("Add at least one more host", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAll_with_no_drift_in_multi_host_scan_reports_clean_summary()
    {
        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = (plans, _, _) =>
            {
                var planList = plans.ToList();
                return Task.FromResult(new ServerScanResponse
                {
                    Results = planList.Select(plan => new ServerScanResult
                    {
                        HostId = plan.HostId,
                        Label = plan.Label,
                        Status = ServerScanStatus.Succeeded,
                        Message = "Completed",
                        Timestamp = DateTimeOffset.UtcNow,
                        Availability = ServerAvailabilityStatus.Found,
                    }).ToArray(),
                    Summary = new ServerScanSummary
                    {
                        BaselineHostId = planList[0].HostId,
                        TotalHosts = planList.Count,
                        ConfigsEvaluated = 20,
                        DriftingConfigs = 0,
                        GeneratedAt = DateTimeOffset.UtcNow,
                    },
                });
            },
        };

        var viewModel = new ServerSelectionViewModel(service, new ToastService(action => action()), new InMemorySessionCacheService());

        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.StatusBanner.Should().Contain("No drift detected across");
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
    public async Task RunAll_requires_active_servers_and_available_run_gate()
    {
        var blockingStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlockingRun = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = async (plans, progress, token) =>
            {
                blockingStart.TrySetResult(true);
                await releaseBlockingRun.Task.ConfigureAwait(false);
                return BuildResponse(plans.First().HostId, plans.ToList());
            },
        };
        var viewModel = new ServerSelectionViewModel(service, new ToastService(action => action()), new InMemorySessionCacheService());
        foreach (var server in viewModel.Servers)
        {
            server.IsEnabled = false;
        }

        await viewModel.RunAllCommand.ExecuteAsync(null);
        viewModel.StatusBanner.Should().Be("Enable at least one server to run.");

        viewModel.Servers[0].IsEnabled = true;
        var firstRun = viewModel.RunAllCommand.ExecuteAsync(null);
        await blockingStart.Task;
        try
        {
            await viewModel.RunAllCommand.ExecuteAsync(null);
            viewModel.StatusBanner.Should().Be("Another multi-server run is already in progress.");
        }
        finally
        {
            releaseBlockingRun.TrySetResult(true);
            await firstRun;
        }
    }

    [Fact]
    public void Root_add_and_remove_guards_update_state()
    {
        var viewModel = new ServerSelectionViewModel(new FakeDriftbusterService(), new ToastService(action => action()), new InMemorySessionCacheService());
        var slot = viewModel.Servers[0];
        slot.Scope = ServerScanScope.CustomRoots;

        slot.NewRootPath = " ";
        viewModel.AddRootCommand.Execute(slot);
        slot.RootInputError.Should().Be("Enter a root path.");

        var uniqueRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "driftbuster-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(uniqueRoot);
        try
        {
            slot.NewRootPath = uniqueRoot;
            viewModel.AddRootCommand.Execute(slot);

            slot.NewRootPath = uniqueRoot;
            viewModel.AddRootCommand.Execute(slot);
            slot.RootInputError.Should().BeNull();
            viewModel.StatusBanner.Should().Contain("already present");
        }
        finally
        {
            if (Directory.Exists(uniqueRoot))
            {
                Directory.Delete(uniqueRoot, recursive: true);
            }
        }

        var loneRoot = slot.Roots.First();
        slot.ReplaceRoots(new[] { loneRoot });
        viewModel.RemoveRootCommand.Execute(loneRoot);

        loneRoot.ValidationState.Should().Be(RootValidationState.Invalid);
        loneRoot.StatusMessage.Should().Be("At least one root is required.");
    }

    [Fact]
    public async Task Save_and_load_session_failures_surface_status_and_activity()
    {
        var loadFailure = new ExplodingSessionCacheService(throwOnLoad: new InvalidOperationException("load-failed"));
        var loadViewModel = new ServerSelectionViewModel(new FakeDriftbusterService(), new ToastService(action => action()), loadFailure);
        SpinWait.SpinUntil(() => loadViewModel.StatusBanner.Contains("Failed to load session", StringComparison.Ordinal), TimeSpan.FromSeconds(2))
            .Should().BeTrue();
        loadViewModel.ActivityEntries.Should().Contain(entry => entry.Summary == "Failed to load session");

        var saveFailure = new ExplodingSessionCacheService(throwOnSave: new InvalidOperationException("save-failed"));
        var saveToast = new ToastService(action => action());
        var saveViewModel = new ServerSelectionViewModel(new FakeDriftbusterService(), saveToast, saveFailure)
        {
            PersistSessionState = true,
        };

        await saveViewModel.SaveSessionCommand.ExecuteAsync(null);

        saveViewModel.StatusBanner.Should().Contain("Failed to save session");
        saveViewModel.ActivityEntries.Should().Contain(entry => entry.Summary == "Failed to save session");
        saveToast.ActiveToasts.Should().Contain(notification => notification.Title == "Session save failed");
    }

    [Fact]
    public async Task ShowDrilldownForHost_reports_blocking_reasons()
    {
        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = (plans, _, _) => Task.FromResult(BuildResponse(plans.First().HostId, plans.ToList())),
        };
        var viewModel = new ServerSelectionViewModel(service, new ToastService(action => action()), new InMemorySessionCacheService());
        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.ShowDrilldownForHostCommand.Execute(null);

        var host = viewModel.Servers[0];
        viewModel.IsBusy = true;
        viewModel.ShowDrilldownForHostCommand.Execute(host.HostId);
        viewModel.StatusBanner.Should().Contain("Finish current scans");

        viewModel.IsBusy = false;
        viewModel.ShowDrilldownForHostCommand.Execute("missing-host");
        viewModel.StatusBanner.Should().Contain("no longer available");

        host.IsEnabled = false;
        viewModel.ShowDrilldownForHostCommand.Execute(host.HostId);
        viewModel.StatusBanner.Should().Contain("Enable the host");
    }

    [Fact]
    public void LogActivity_enforces_max_activity_item_limit()
    {
        var viewModel = new ServerSelectionViewModel(new FakeDriftbusterService(), new ToastService(action => action()), new InMemorySessionCacheService());
        var server = viewModel.Servers[0];
        for (var i = 0; i < 260; i++)
        {
            server.NewRootPath = " ";
            viewModel.AddRootCommand.Execute(server);
        }

        viewModel.ActivityEntries.Count.Should().Be(200);
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
        var server = viewModel.Servers[0];

        viewModel.UseVirtualizedActivityFeed.Should().BeFalse();
        viewModel.UseVirtualizedServerList.Should().BeTrue();

        server.NewRootPath = " ";
        viewModel.AddRootCommand.Execute(server);
        viewModel.UseVirtualizedActivityFeed.Should().BeFalse();

        server.NewRootPath = " ";
        viewModel.AddRootCommand.Execute(server);
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
        viewModel.ShowDrilldownForHostCommand.CanExecute(hostId).Should().BeTrue();
        viewModel.ShowDrilldownForHostCommand.Execute(hostId);
        viewModel.DrilldownViewModel.Should().NotBeNull();
        viewModel.DrilldownViewModel!.Servers.Should().Contain(server =>
            string.Equals(server.HostId, hostId, StringComparison.OrdinalIgnoreCase) && server.Present);
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

    private sealed class ExplodingSessionCacheService : ISessionCacheService
    {
        private readonly Exception? _throwOnLoad;
        private readonly Exception? _throwOnSave;

        public ExplodingSessionCacheService(Exception? throwOnLoad = null, Exception? throwOnSave = null)
        {
            _throwOnLoad = throwOnLoad;
            _throwOnSave = throwOnSave;
        }

        public Task<ServerSelectionCache?> LoadAsync(CancellationToken cancellationToken = default)
            => _throwOnLoad is null
                ? Task.FromResult<ServerSelectionCache?>(new ServerSelectionCache())
                : Task.FromException<ServerSelectionCache?>(_throwOnLoad);

        public Task SaveAsync(ServerSelectionCache snapshot, CancellationToken cancellationToken = default)
            => _throwOnSave is null ? Task.CompletedTask : Task.FromException(_throwOnSave);

        public void Clear()
        {
        }
    }

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
