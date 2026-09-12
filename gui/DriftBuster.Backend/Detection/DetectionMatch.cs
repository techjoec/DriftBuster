namespace DriftBuster.Backend.Detection;

/// <summary>Result produced by a format plugin for a sampled file.</summary>
public sealed class DetectionMatch
{
    public DetectionMatch(
        string pluginName,
        string formatName,
        string? variant,
        double confidence,
        IEnumerable<string> reasons,
        OrderedDictionary<string, object?>? metadata = null)
    {
        PluginName = pluginName;
        FormatName = formatName;
        Variant = variant;
        Confidence = confidence;
        Reasons = reasons as List<string> ?? new List<string>(reasons);
        Metadata = metadata;
    }

    public string PluginName { get; set; }

    public string FormatName { get; set; }

    public string? Variant { get; set; }

    public double Confidence { get; set; }

    public IList<string> Reasons { get; set; }

    /// <summary>Insertion-ordered metadata; keys are compared ordinally, mirroring a Python dict.</summary>
    public OrderedDictionary<string, object?>? Metadata { get; set; }

    /// <summary>A copy of the match with plugin, format, variant, confidence, reasons and metadata keys.</summary>
    public OrderedDictionary<string, object?> ToDictionary()
    {
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["plugin"] = PluginName,
            ["format"] = FormatName,
            ["variant"] = Variant,
            ["confidence"] = Confidence,
            ["reasons"] = new List<string>(Reasons),
            ["metadata"] = Metadata is null ? null : new OrderedDictionary<string, object?>(Metadata, StringComparer.Ordinal),
        };
    }
}
