using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Backend.Settings;

/// <summary>
/// Reads a scanned file's settings with the reader for its detected format, falling back to <see cref="BlockSettings"/> when the
/// format has no reader or the file does not parse. Never throws for file content.
/// </summary>
public static class SettingsExtractor
{
    public static ExtractedSettings Extract(ConfigRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Extract(record.FormatId, record.PluginName, record.Raw, record.FileHash);
    }

    public static ExtractedSettings Extract(string formatId, string pluginName, string text, string fileHash)
    {
        ArgumentNullException.ThrowIfNull(formatId);
        ArgumentNullException.ThrowIfNull(pluginName);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fileHash);
        if (IsBinary(formatId, pluginName))
        {
            return new ExtractedSettings(SettingsMode.Binary, [new SettingEntry("(file contents)", "sha256:" + fileHash)]);
        }

        var parsed = string.Equals(pluginName, "script", StringComparison.Ordinal) ? ScriptSettings.Extract(text) : formatId switch
        {
            "structured-config-xml" or "xml" => XmlSettings.Extract(text),
            "json" => JsonSettings.Extract(text),
            "yaml" => YamlSettings.Extract(text),
            "toml" => TomlSettings.Extract(text),
            "ini" => IniSettings.Extract(text),
            "registry-export" => RegistryExportSettings.Extract(text),
            "properties" => IniSettings.Extract(text, continuations: true),
            "registry-live" => JsonSettings.Extract(text) ?? YamlSettings.Extract(text),
            _ => pluginName switch
            {
                "xml" => XmlSettings.Extract(text),
                "json" => JsonSettings.Extract(text),
                "yaml" => YamlSettings.Extract(text),
                "toml" => TomlSettings.Extract(text),
                "ini" => IniSettings.Extract(text),
                _ => null,
            },
        };
        var settings = parsed ?? BlockSettings.Extract(text);
        return settings.Truncated
            ? settings with { Entries = [.. settings.Entries, new SettingEntry(RestOfFileKey, "sha256:" + fileHash)] }
            : settings;
    }

    /// <summary>The row that compares a cut-short file as a whole, so a difference past the cut is not lost.</summary>
    public const string RestOfFileKey = "(rest of the file, compared as a whole)";

    private static bool IsBinary(string formatId, string pluginName) =>
        formatId is "embedded-sql-db" or "binary-dat" or "plist" || string.Equals(pluginName, "binary-hybrid", StringComparison.Ordinal);
}
