using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// <c>registry.RegistryRoot</c>: a hive (<c>HKLM</c> or <c>HKCU</c>), a key path under it and an optional view (<c>"32"</c>,
/// <c>"64"</c>, or null for the default). It is also the <c>(hive, path, view)</c> tuple <c>find_app_registry_roots</c> returns and
/// <c>search_registry</c> walks; equality is by all three, ordinal.
/// </summary>
public sealed partial record RegistryRoot(string Hive, string Path, string? View = null)
{
    [GeneratedRegex(
        @"\A(?<hive>HKLM|HKCU)\\(?<path>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DescriptorPattern();

    /// <summary><c>root.as_tuple()</c>.</summary>
    public (string Hive, string Path, string? View) AsTuple() => (Hive, Path, View);

    /// <summary>
    /// <c>parse_registry_root_descriptor(text)</c>: <c>HIVE\path[,view=32|64|auto]</c> with "/" read as "\", the hive matched
    /// case-insensitively and upper-cased, the path stripped, and <c>view=auto</c> read as no view (the last view option wins).
    /// </summary>
    /// <exception cref="EngineValueException">Python's <c>ValueError</c> text for each refusal.</exception>
    /// <exception cref="EngineIndexException">A descriptor of only commas and whitespace (Python's <c>IndexError</c>).</exception>
    public static RegistryRoot Parse(string? text)
    {
        var value = EngineText.Strip(text ?? string.Empty);
        if (value.Length == 0)
        {
            throw new EngineValueException("Registry root descriptor must be non-empty", nameof(text));
        }

        var segments = value.Split(',').Select(EngineText.Strip).Where(segment => segment.Length > 0).ToList();
        if (segments.Count == 0)
        {
            throw new EngineIndexException(nameof(text), "list index out of range");
        }

        var match = DescriptorPattern().Match(segments[0].Replace('/', '\\'));
        if (!match.Success)
        {
            throw new EngineValueException("Registry root descriptor must start with HKLM\\ or HKCU\\", nameof(text));
        }

        var hive = RegistryText.Upper(match.Groups["hive"].Value);
        var path = EngineText.Strip(match.Groups["path"].Value);
        if (path.Length == 0)
        {
            throw new EngineValueException("Registry root path segment must be non-empty", nameof(text));
        }

        string? view = null;
        foreach (var option in segments.Skip(1))
        {
            view = ParseOption(option);
        }

        return new RegistryRoot(hive, path, view);
    }

    private static string? ParseOption(string option)
    {
        var separator = option.IndexOf('=', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new EngineValueException($"Registry root option '{option}' must be formatted as key=value", nameof(option));
        }

        var key = EngineText.Lower(EngineText.Strip(option[..separator]));
        var rawValue = EngineText.Strip(option[(separator + 1)..]);
        if (!string.Equals(key, "view", StringComparison.Ordinal))
        {
            throw new EngineValueException($"Unsupported registry root option '{key}'", nameof(option));
        }

        if (rawValue.Length == 0)
        {
            throw new EngineValueException("Registry root view must be non-empty when provided", nameof(option));
        }

        return RegistryText.Upper(rawValue) switch
        {
            "AUTO" => null,
            "32" => "32",
            "64" => "64",
            _ => throw new EngineValueException("Registry root view must be 32, 64, or auto", nameof(option)),
        };
    }
}
