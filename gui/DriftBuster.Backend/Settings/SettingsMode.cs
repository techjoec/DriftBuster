namespace DriftBuster.Backend.Settings;

/// <summary>How a file's settings were read.</summary>
public enum SettingsMode
{
    /// <summary>A format reader named each setting (XML, JSON, INI, YAML, TOML).</summary>
    Parsed,

    /// <summary>Directive or key lines, nested by braces where the file uses them (conf, HCL, Dockerfile, plain text).</summary>
    Lines,

    /// <summary>A binary file: one entry, its content hash.</summary>
    Binary,
}
