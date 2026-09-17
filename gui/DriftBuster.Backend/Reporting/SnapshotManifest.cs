using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Reporting;

/// <summary>A detection manifest with the legal block and redaction state embedded.</summary>
public static class SnapshotManifest
{
    /// <summary><c>datetime.now(UTC)</c>, swapped by tests that pin <c>generated_at</c>.</summary>
    internal static Func<EngineDateTime> UtcNow { get; set; } = EngineDateTime.UtcNow;

    /// <summary>
    /// <c>build_snapshot_manifest</c>: <c>generated_at</c>, <c>output</c>, <c>operator</c>, <c>legal</c> (the default classification,
    /// placeholder, retention, disposal and warnings, updated with <paramref name="legalMetadata"/>, plus <c>redacted_tokens</c> when
    /// the redactor replaced anything) and <c>matches</c> (the JSON line records), then <c>run_metadata</c> when there is any: the extra
    /// metadata with <c>operator</c> and <c>snapshot_output</c> added unless already present.
    /// </summary>
    public static OrderedDictionary<string, object?> Build(
        IEnumerable<DetectionMatch> matches,
        string? outputName = null,
        string? @operator = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? legalMetadata = null,
        IReadOnlyDictionary<string, object?>? extraMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var activeRedactor = RedactionFilter.Resolve(redactor, maskTokens, placeholder);
        var runMetadata = ReportValues.Copy(extraMetadata ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal));
        if (!string.IsNullOrEmpty(@operator))
        {
            runMetadata.TryAdd("operator", @operator);
        }

        if (!string.IsNullOrEmpty(outputName))
        {
            runMetadata.TryAdd("snapshot_output", outputName);
        }

        var records = JsonLinesReport.IterJsonRecords(matches, redactor: activeRedactor, extraMetadata: runMetadata.Count > 0 ? runMetadata : null)
            .Cast<object?>()
            .ToList();
        var legalBlock = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["classification"] = "internal-only",
            ["redaction_placeholder"] = placeholder,
            ["retention_days"] = 30,
            ["disposal"] = "Securely delete snapshots after review or when superseded.",
            ["warnings"] = new List<object?>
            {
                "Do not forward snapshots outside the trusted review group.",
                "Confirm redaction before sharing derived artefacts.",
            },
        };
        foreach (var (key, value) in legalMetadata ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal))
        {
            legalBlock[key] = value;
        }

        if (activeRedactor is { HasHits: true })
        {
            legalBlock["redacted_tokens"] = activeRedactor.Stats().Aggregate(
                new OrderedDictionary<string, object?>(StringComparer.Ordinal),
                (stats, pair) =>
                {
                    stats[pair.Key] = pair.Value;
                    return stats;
                });
        }

        var manifest = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["generated_at"] = UtcNow().IsoFormat(),
            ["output"] = outputName,
            ["operator"] = @operator,
            ["legal"] = legalBlock,
            ["matches"] = records,
        };
        if (runMetadata.Count > 0)
        {
            manifest["run_metadata"] = runMetadata;
        }

        return manifest;
    }

    /// <summary>
    /// <c>write_snapshot</c>: the manifest of <see cref="Build"/> written to <paramref name="destination"/> as
    /// <c>json.dump(manifest, ensure_ascii=False, indent=indent)</c> and a line break, in text mode, after creating the parent
    /// directories; a write failure raises Python's <c>OSError</c> text.
    /// </summary>
    public static void Write(
        IEnumerable<DetectionMatch> matches,
        string destination,
        string? @operator = null,
        string? outputName = null,
        RedactionFilter? redactor = null,
        IReadOnlyList<string>? maskTokens = null,
        string placeholder = RedactionFilter.DefaultPlaceholder,
        IReadOnlyDictionary<string, object?>? legalMetadata = null,
        int indent = 2,
        IReadOnlyDictionary<string, object?>? extraMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var manifest = Build(matches, outputName, @operator, redactor, maskTokens, placeholder, legalMetadata, extraMetadata);
        var text = ReportValues.DumpsIndented(manifest, indent, ensureAscii: false) + "\n";
        var parent = EnginePurePath.Parent(destination);
        EnginePath.MakeDirectories(parent);
        EngineTextFile.WriteText(destination, ReportValues.TextModeNewLines(text));
    }
}
