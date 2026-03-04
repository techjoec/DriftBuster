#if DEBUG
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Services;

/// <summary>
/// Routes JSON automation commands to ViewModel methods.
/// All public methods are called on the Avalonia UI thread.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class AutomationDispatcher
{
    private readonly MainWindowViewModel _mainVm;
    private ServerSelectionViewModel? _serverVm;

    public AutomationDispatcher(MainWindowViewModel mainVm)
    {
        _mainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
    }

    public string Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("cmd", out var cmdElement))
            {
                return AutomationState.Serialize(AutomationState.ErrorResponse("Missing 'cmd' property."));
            }

            var cmd = cmdElement.GetString() ?? string.Empty;
            var result = cmd switch
            {
                "ping" => HandlePing(),
                "get-state" => HandleGetState(),
                "navigate" => HandleNavigate(root),
                "set-enabled" => HandleSetEnabled(root),
                "set-scope" => HandleSetScope(root),
                "set-label" => HandleSetLabel(root),
                "add-root" => HandleAddRoot(root),
                "remove-root" => HandleRemoveRoot(root),
                "run-all" => HandleRunAll(),
                "run-missing" => HandleRunMissing(),
                "cancel" => HandleCancel(),
                "clear-history" => HandleClearHistory(),
                "get-catalog" => HandleGetCatalog(),
                "get-activity" => HandleGetActivity(),
                "get-drilldown" => HandleGetDrilldown(root),
                _ => AutomationState.ErrorResponse($"Unknown command: {cmd}"),
            };

            return AutomationState.Serialize(result);
        }
        catch (JsonException ex)
        {
            return AutomationState.Serialize(AutomationState.ErrorResponse($"Invalid JSON: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return AutomationState.Serialize(AutomationState.ErrorResponse($"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private ServerSelectionViewModel? GetServerVm()
    {
        // Always re-probe CurrentView to avoid holding a stale reference
        // when the user navigates via the GUI (bypassing automation commands).
        if (_mainVm.CurrentView is Avalonia.Controls.Control control &&
            control.DataContext is ServerSelectionViewModel vm)
        {
            _serverVm = vm;
            return vm;
        }

        // Fall back to cached reference if CurrentView has changed away
        // but we still need to read from the last known multi-server VM.
        return _serverVm;
    }

    private ServerSelectionViewModel RequireServerVm()
    {
        return GetServerVm() ?? throw new InvalidOperationException("Navigate to multi-server tab first.");
    }

    private static int GetSlotIndex(JsonElement root)
    {
        if (!root.TryGetProperty("slot", out var slotEl))
        {
            throw new ArgumentException("Missing 'slot' property.", nameof(root));
        }

        return slotEl.GetInt32();
    }

    private ServerSlotViewModel GetSlot(JsonElement root)
    {
        var vm = RequireServerVm();
        var index = GetSlotIndex(root);
        if (index < 0 || index >= vm.Servers.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(root), $"Slot index {index} out of range (0..{vm.Servers.Count - 1}).");
        }

        return vm.Servers[index];
    }

    private Dictionary<string, object?> HandlePing()
    {
        return AutomationState.OkResponse(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["pid"] = Environment.ProcessId,
        });
    }

    private Dictionary<string, object?> HandleGetState()
    {
        var serverVm = GetServerVm();
        var state = AutomationState.SnapshotState(_mainVm, serverVm);
        return AutomationState.OkResponse(state);
    }

    private Dictionary<string, object?> HandleNavigate(JsonElement root)
    {
        if (!root.TryGetProperty("tab", out var tabEl))
        {
            return AutomationState.ErrorResponse("Missing 'tab' property.");
        }

        var tab = tabEl.GetString() ?? string.Empty;
        var (section, subView) = ParseNavigationTarget(tab);

        switch (section)
        {
            case "diff":
                _mainVm.ShowDiff();
                _serverVm = null;
                break;
            case "hunt":
                _mainVm.ShowHunt();
                _serverVm = null;
                break;
            case "profiles":
                _mainVm.ShowProfiles();
                _serverVm = null;
                break;
            case "multi-server":
            case "multiserver":
                _mainVm.ShowMultiServer();
                _serverVm = null;
                return NavigateMultiServerSubView(subView);
            default:
                return AutomationState.ErrorResponse(
                    $"Unknown tab: {tab}. Valid: diff, hunt, profiles, multi-server, multi-server/setup, multi-server/catalog, multi-server/drilldown.");
        }

        return AutomationState.OkResponse();
    }

    private static (string Section, string? SubView) ParseNavigationTarget(string tab)
    {
        var normalized = tab.Trim().ToLowerInvariant();
        if (normalized.StartsWith("multi-server/", StringComparison.Ordinal))
        {
            return ("multi-server", normalized["multi-server/".Length..]);
        }

        if (normalized.StartsWith("multiserver/", StringComparison.Ordinal))
        {
            return ("multi-server", normalized["multiserver/".Length..]);
        }

        return (normalized, null);
    }

    private Dictionary<string, object?> NavigateMultiServerSubView(string? subView)
    {
        if (string.IsNullOrWhiteSpace(subView))
        {
            return AutomationState.OkResponse();
        }

        var vm = RequireServerVm();
        var target = subView.Trim();

        switch (target)
        {
            case "setup":
                vm.ShowSetupCommand.Execute(null);
                return AutomationState.OkResponse();
            case "catalog":
                if (!vm.ShowCatalogCommand.CanExecute(null))
                {
                    return AutomationState.ErrorResponse("Catalog view is unavailable (no catalog entries).");
                }

                vm.ShowCatalogCommand.Execute(null);
                return AutomationState.OkResponse();
            case "drilldown":
                if (!vm.ShowDrilldownCommand.CanExecute(null))
                {
                    return AutomationState.ErrorResponse("Drilldown view is unavailable (no drilldown loaded).");
                }

                vm.ShowDrilldownCommand.Execute(null);
                return AutomationState.OkResponse();
            default:
                return AutomationState.ErrorResponse(
                    $"Unknown multi-server sub-view: {subView}. Valid: setup, catalog, drilldown.");
        }
    }

    private Dictionary<string, object?> HandleSetEnabled(JsonElement root)
    {
        var slot = GetSlot(root);
        if (!root.TryGetProperty("enabled", out var enabledEl))
        {
            return AutomationState.ErrorResponse("Missing 'enabled' property.");
        }

        slot.IsEnabled = enabledEl.GetBoolean();
        return AutomationState.OkResponse(AutomationState.SnapshotSlot(slot));
    }

    private Dictionary<string, object?> HandleSetScope(JsonElement root)
    {
        var slot = GetSlot(root);
        if (!root.TryGetProperty("scope", out var scopeEl))
        {
            return AutomationState.ErrorResponse("Missing 'scope' property.");
        }

        var scopeStr = scopeEl.GetString() ?? string.Empty;
        if (!Enum.TryParse<ServerScanScope>(scopeStr, ignoreCase: true, out var scope))
        {
            return AutomationState.ErrorResponse($"Invalid scope: {scopeStr}. Valid: AllDrives, SingleDrive, CustomRoots.");
        }

        slot.Scope = scope;
        return AutomationState.OkResponse(AutomationState.SnapshotSlot(slot));
    }

    private Dictionary<string, object?> HandleSetLabel(JsonElement root)
    {
        var slot = GetSlot(root);
        if (!root.TryGetProperty("label", out var labelEl))
        {
            return AutomationState.ErrorResponse("Missing 'label' property.");
        }

        slot.Label = labelEl.GetString() ?? string.Empty;
        return AutomationState.OkResponse(AutomationState.SnapshotSlot(slot));
    }

    private Dictionary<string, object?> HandleAddRoot(JsonElement root)
    {
        var vm = RequireServerVm();
        var slot = GetSlot(root);
        if (!root.TryGetProperty("path", out var pathEl))
        {
            return AutomationState.ErrorResponse("Missing 'path' property.");
        }

        slot.NewRootPath = pathEl.GetString() ?? string.Empty;
        vm.AddRootCommand.Execute(slot);
        return AutomationState.OkResponse(AutomationState.SnapshotSlot(slot));
    }

    private Dictionary<string, object?> HandleRemoveRoot(JsonElement root)
    {
        var vm = RequireServerVm();
        var slot = GetSlot(root);
        if (!root.TryGetProperty("index", out var indexEl))
        {
            return AutomationState.ErrorResponse("Missing 'index' property.");
        }

        var rootIndex = indexEl.GetInt32();
        if (rootIndex < 0 || rootIndex >= slot.Roots.Count)
        {
            return AutomationState.ErrorResponse($"Root index {rootIndex} out of range (0..{slot.Roots.Count - 1}).");
        }

        vm.RemoveRootCommand.Execute(slot.Roots[rootIndex]);
        return AutomationState.OkResponse(AutomationState.SnapshotSlot(slot));
    }

    private Dictionary<string, object?> HandleRunAll()
    {
        var vm = RequireServerVm();
        if (!vm.RunAllCommand.CanExecute(null))
        {
            return AutomationState.ErrorResponse("RunAll not available (busy or no active servers).");
        }

        _ = vm.RunAllCommand.ExecuteAsync(null);
        return AutomationState.OkResponse(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["planCount"] = vm.Servers.Count(s => s.IsEnabled),
        });
    }

    private Dictionary<string, object?> HandleRunMissing()
    {
        var vm = RequireServerVm();
        if (!vm.RunMissingCommand.CanExecute(null))
        {
            return AutomationState.ErrorResponse("RunMissing not available.");
        }

        _ = vm.RunMissingCommand.ExecuteAsync(null);
        return AutomationState.OkResponse();
    }

    private Dictionary<string, object?> HandleCancel()
    {
        var vm = RequireServerVm();
        if (vm.CancelRunsCommand.CanExecute(null))
        {
            vm.CancelRunsCommand.Execute(null);
        }

        return AutomationState.OkResponse();
    }

    private Dictionary<string, object?> HandleClearHistory()
    {
        var vm = RequireServerVm();
        vm.ClearHistoryCommand.Execute(null);
        return AutomationState.OkResponse();
    }

    private Dictionary<string, object?> HandleGetCatalog()
    {
        var vm = RequireServerVm();
        var entries = AutomationState.SnapshotCatalog(vm.CatalogViewModel);
        return AutomationState.OkResponse(entries);
    }

    private Dictionary<string, object?> HandleGetActivity()
    {
        var vm = RequireServerVm();
        var entries = AutomationState.SnapshotActivity(vm.FilteredActivityEntries);
        return AutomationState.OkResponse(entries);
    }

    private Dictionary<string, object?> HandleGetDrilldown(JsonElement root)
    {
        var vm = RequireServerVm();
        if (!root.TryGetProperty("hostId", out var hostIdEl))
        {
            return AutomationState.ErrorResponse("Missing 'hostId' property.");
        }

        var hostId = hostIdEl.GetString() ?? string.Empty;
        if (!vm.ShowDrilldownForHostCommand.CanExecute(hostId))
        {
            return AutomationState.ErrorResponse($"Cannot show drilldown for host '{hostId}'.");
        }

        vm.ShowDrilldownForHostCommand.Execute(hostId);

        if (vm.DrilldownViewModel is null)
        {
            return AutomationState.ErrorResponse("Drilldown not available after navigation.");
        }

        var drilldown = vm.DrilldownViewModel;
        return AutomationState.OkResponse(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["configId"] = drilldown.ConfigId,
            ["displayName"] = drilldown.DisplayName,
            ["format"] = drilldown.Format,
            ["driftCount"] = drilldown.DriftCount,
            ["hasSecrets"] = drilldown.HasSecrets,
            ["baselineHostId"] = drilldown.BaselineHostId,
            ["baselineLabel"] = drilldown.BaselineLabel,
        });
    }
}
#endif
