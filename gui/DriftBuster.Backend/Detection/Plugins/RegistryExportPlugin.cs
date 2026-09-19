using System.Text.Json.Nodes;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects Windows Registry Editor exports (<c>.reg</c>). The header line (<c>Windows Registry Editor Version 5.00</c> or
/// <c>REGEDIT4</c>) is required, since regedit refuses a file without it; <c>[HKEY_…]</c> sections and the <c>.reg</c>
/// extension raise the confidence. Version 5 exports are UTF-16LE with a byte order mark and are decoded here.
/// </summary>
/// <remarks>Derived from publicly documented behavior, not vendor source.</remarks>
public sealed class RegistryExportPlugin : IFormatPlugin
{
    private const string Extension = ".reg";
    private const int HivesPreviewLimit = 5;

    public string Name => "registry-export";

    public int Priority => 20;

    public string Version => "0.0.1";

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sample);
        var content = RegistryExportText.SampleText(sample, text);
        var version = content is null ? null : RegistryExportText.HeaderVersion(content);
        if (content is null || version is null)
        {
            return null;
        }

        var reasons = new List<string> { $"Found Registry Editor export header (version {version})" };
        var counts = Count(content);
        var confidence = 0.7;
        if (counts.Hives.Count > 0)
        {
            reasons.Add("Found registry key sections under " + string.Join(", ", counts.Hives.Take(HivesPreviewLimit)));
            confidence += 0.15;
        }

        if (string.Equals(PathText.SuffixLower(path), Extension, StringComparison.Ordinal))
        {
            reasons.Add("File extension .reg suggests a Registry Editor export");
            confidence += 0.1;
        }

        var metadata = new JsonObject()
        {
            ["registry_editor_version"] = version,
            ["hives"] = JsonNodes.Strings(counts.Hives.Take(HivesPreviewLimit).ToList()),
            ["key_count"] = (long)counts.Keys,
            ["value_count"] = (long)counts.Values,
        };
        if (counts.DeletedKeys > 0 || counts.DeletedValues > 0)
        {
            metadata["deleted_keys"] = (long)counts.DeletedKeys;
            metadata["deleted_values"] = (long)counts.DeletedValues;
            reasons.Add("Export deletes registry keys or values when imported");
        }

        var variant = string.Equals(version, "4", StringComparison.Ordinal) ? "regedit4" : "regedit5";
        return new DetectionMatch(Name, "registry-export", variant, Math.Min(0.95, confidence), reasons, metadata);
    }

    private sealed record Counts(int Keys, int DeletedKeys, int Values, int DeletedValues, SortedSet<string> Hives);

    // Sections, values and deletions in the sampled text, with the HKEY_ roots the sections sit under.
    private static Counts Count(string content)
    {
        var keys = 0;
        var deletedKeys = 0;
        var values = 0;
        var deletedValues = 0;
        var hives = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in RegistryExportText.Entries(content))
        {
            if (entry.ValueName is null)
            {
                keys++;
                deletedKeys += entry.KeyDeleted ? 1 : 0;
                var root = entry.KeyPath.Split('\\', 2)[0].ToUpperInvariant();
                if (root.StartsWith("HKEY_", StringComparison.Ordinal))
                {
                    hives.Add(root);
                }

                continue;
            }

            values++;
            deletedValues += string.Equals(entry.RawData, "-", StringComparison.Ordinal) ? 1 : 0;
        }

        return new Counts(keys, deletedKeys, values, deletedValues, hives);
    }
}
