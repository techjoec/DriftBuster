#if DEBUG
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Services;

/// <summary>
/// Snapshots ViewModel state into JSON-serializable DTOs for the automation pipe.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class AutomationState
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(object value) =>
        JsonSerializer.Serialize(value, s_options);

    public static Dictionary<string, object?> SnapshotState(MainWindowViewModel mainVm, ServerSelectionViewModel? serverVm)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["activeTab"] = mainVm.ActiveView.ToString().ToLowerInvariant(),
        };

        if (serverVm is not null)
        {
            state["isBusy"] = serverVm.IsBusy;
            state["statusBanner"] = serverVm.StatusBanner;
            state["isViewingCatalog"] = serverVm.IsViewingCatalog;
            state["isViewingDrilldown"] = serverVm.IsViewingDrilldown;
            state["hasCatalogEntries"] = serverVm.CatalogViewModel.HasEntries;
            state["slots"] = serverVm.Servers.Select(SnapshotSlot).ToArray();
        }

        return state;
    }

    public static Dictionary<string, object?> SnapshotSlot(ServerSlotViewModel slot)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["index"] = slot.Index,
            ["hostId"] = slot.HostId,
            ["label"] = slot.Label,
            ["isEnabled"] = slot.IsEnabled,
            ["scope"] = slot.Scope.ToString(),
            ["runState"] = slot.RunState.ToString(),
            ["statusText"] = slot.StatusText,
            ["validationSummary"] = slot.ValidationSummary,
            ["roots"] = slot.Roots.Select(r => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = r.Path,
                ["validationState"] = r.ValidationState.ToString(),
                ["statusMessage"] = r.StatusMessage,
            }).ToArray(),
        };
    }

    public static object[] SnapshotCatalog(ResultsCatalogViewModel catalog)
    {
        return catalog.FilteredEntries.Select(entry => (object)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["configId"] = entry.ConfigId,
            ["displayName"] = entry.DisplayName,
            ["format"] = entry.Format,
            ["driftCount"] = entry.DriftCount,
            ["severity"] = entry.Severity,
            ["coverageText"] = entry.CoverageText,
            ["coverageStatus"] = entry.CoverageStatus,
            ["hasSecrets"] = entry.HasSecrets,
            ["hasValidationIssues"] = entry.HasValidationIssues,
            ["presentHosts"] = entry.PresentHosts,
            ["missingHosts"] = entry.MissingHosts,
        }).ToArray();
    }

    public static object[] SnapshotActivity(IEnumerable<ActivityEntryViewModel> entries)
    {
        return entries.Select(entry => (object)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["severity"] = entry.Severity.ToString(),
            ["summary"] = entry.Summary,
            ["detail"] = entry.Detail,
            ["timestamp"] = entry.Timestamp,
            ["category"] = entry.Category.ToString(),
        }).ToArray();
    }

    public static Dictionary<string, object?> OkResponse(object? data = null)
    {
        var response = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ok"] = true };
        if (data is not null)
        {
            response["data"] = data;
        }

        return response;
    }

    public static Dictionary<string, object?> ErrorResponse(string message)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ok"] = false,
            ["error"] = message,
        };
    }
}
#endif
