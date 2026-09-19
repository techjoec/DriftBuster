using System.Text.Json.Nodes;

namespace DriftBuster.Backend.Detection;

/// <summary>Result produced by a format plugin for a sampled file.</summary>
public sealed class DetectionMatch(
    string pluginName,
    string formatName,
    string? variant,
    double confidence,
    IEnumerable<string> reasons,
    JsonObject? metadata = null)
{
    public string PluginName { get; set; } = pluginName;

    public string FormatName { get; set; } = formatName;

    public string? Variant { get; set; } = variant;

    public double Confidence { get; set; } = confidence;

    public IList<string> Reasons { get; set; } = reasons as List<string> ?? [.. reasons];

    /// <summary>What the plugin found, in the order it was added, plus the detector's sampling notes and the catalog enrichment.</summary>
    public JsonObject Metadata { get; set; } = metadata ?? [];

    /// <summary>A copy for output: the file, plugin, format, variant, confidence, reasons and metadata.</summary>
    public DetectionPayload ToPayload(string path) => new(path, PluginName, FormatName, Variant, Confidence, [.. Reasons], (JsonObject)Metadata.DeepClone());
}
