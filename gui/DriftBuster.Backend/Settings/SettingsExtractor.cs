using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Backend.Settings;

/// <summary>
/// Reads a scanned file's settings with the reader for its detected format, or with the reader the user chose ("Parse as"),
/// falling back to <see cref="BlockSettings"/> when the reader does not apply or the file does not parse. Never throws for
/// file content.
/// </summary>
public static class SettingsExtractor
{
    /// <summary>The settings readers, by the name a "Parse as" choice stores.</summary>
    public static class Readers
    {
        public const string Xml = "xml";
        public const string Json = "json";
        public const string Yaml = "yaml";
        public const string Toml = "toml";
        public const string Ini = "ini";
        public const string Properties = "properties";
        public const string RegistryExport = "registry-export";
        public const string Script = "script";
        public const string Lines = "lines";
        public const string Binary = "binary";

        /// <summary>A live registry scan config: JSON, else YAML. Detection-only; "Parse as" offers JSON and YAML separately.</summary>
        public const string RegistryLive = "registry-live";

        /// <summary>Every reader a file can be parsed as, in the order "Parse as" lists them.</summary>
        public static IReadOnlyList<string> All { get; } = [Xml, Json, Yaml, Toml, Ini, Properties, RegistryExport, Script, Lines];
    }

    public static ExtractedSettings Extract(ConfigRecord record, string? reader = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        return ExtractAs(reader ?? ReaderFor(record.FormatId, record.PluginName), record.Raw, record.FileHash);
    }

    public static ExtractedSettings Extract(string formatId, string pluginName, string text, string fileHash)
    {
        ArgumentNullException.ThrowIfNull(formatId);
        ArgumentNullException.ThrowIfNull(pluginName);
        return ExtractAs(ReaderFor(formatId, pluginName), text, fileHash);
    }

    /// <summary>The reader detection picks for a format: the plugin's own, else the format's, else <see cref="Readers.Lines"/>.</summary>
    public static string ReaderFor(string formatId, string pluginName)
    {
        ArgumentNullException.ThrowIfNull(formatId);
        ArgumentNullException.ThrowIfNull(pluginName);
        if (IsBinary(formatId, pluginName))
        {
            return Readers.Binary;
        }

        if (string.Equals(pluginName, "script", StringComparison.Ordinal))
        {
            return Readers.Script;
        }

        return formatId switch
        {
            "structured-config-xml" or "xml" => Readers.Xml,
            "json" => Readers.Json,
            "registry-live" => Readers.RegistryLive,
            "yaml" => Readers.Yaml,
            "toml" => Readers.Toml,
            "ini" => Readers.Ini,
            "properties" => Readers.Properties,
            "registry-export" => Readers.RegistryExport,
            _ => pluginName switch
            {
                "xml" => Readers.Xml,
                "json" => Readers.Json,
                "yaml" => Readers.Yaml,
                "toml" => Readers.Toml,
                "ini" => Readers.Ini,
                _ => Readers.Lines,
            },
        };
    }

    /// <summary>The file's settings read with <paramref name="reader"/> (one of <see cref="Readers"/>).</summary>
    public static ExtractedSettings ExtractAs(string reader, string text, string fileHash)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fileHash);
        if (string.Equals(reader, Readers.Binary, StringComparison.Ordinal))
        {
            return new ExtractedSettings(SettingsMode.Binary, [new SettingEntry("(file contents)", "sha256:" + fileHash)]);
        }

        var parsed = reader switch
        {
            Readers.Xml => XmlSettings.Extract(text),
            Readers.Json => JsonSettings.Extract(text),
            Readers.RegistryLive => JsonSettings.Extract(text) ?? YamlSettings.Extract(text),
            Readers.Yaml => YamlSettings.Extract(text),
            Readers.Toml => TomlSettings.Extract(text),
            Readers.Ini => IniSettings.Extract(text),
            Readers.Properties => IniSettings.Extract(text, continuations: true),
            Readers.RegistryExport => RegistryExportSettings.Extract(text),
            Readers.Script => ScriptSettings.Extract(text),
            _ => null,
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
