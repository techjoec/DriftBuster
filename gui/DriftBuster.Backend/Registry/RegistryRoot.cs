using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// <c>registry.RegistryRoot</c>: a hive (<c>HKLM</c> or <c>HKCU</c>), a key path under it and an optional view (<c>"32"</c>,
/// <c>"64"</c>, or null for the default). It is also the <c>(hive, path, view)</c> tuple <c>find_app_registry_roots</c> returns and
/// <c>search_registry</c> walks; equality is by all three, ordinal.
/// </summary>
public sealed record RegistryRoot(string Hive, string Path, string? View = null)
{
    private static readonly PythonPattern DescriptorPattern = PythonPattern.Compile(@"^(HKLM|HKCU)\\(.+)$", PythonReFlags.IgnoreCase);

    /// <summary><c>root.as_tuple()</c>.</summary>
    public (string Hive, string Path, string? View) AsTuple() => (Hive, Path, View);

    /// <summary>
    /// <c>parse_registry_root_descriptor(text)</c>: <c>HIVE\path[,view=32|64|auto]</c> with "/" read as "\", the hive matched
    /// case-insensitively and upper-cased, the path stripped, and <c>view=auto</c> read as no view (the last view option wins).
    /// </summary>
    /// <exception cref="PythonValueException">Python's <c>ValueError</c> text for each refusal.</exception>
    /// <exception cref="PythonIndexException">A descriptor of only commas and whitespace (Python's <c>IndexError</c>).</exception>
    public static RegistryRoot Parse(string? text)
    {
        var value = PythonText.Strip(text ?? string.Empty);
        if (value.Length == 0)
        {
            throw new PythonValueException("Registry root descriptor must be non-empty", nameof(text));
        }

        var segments = value.Split(',').Select(PythonText.Strip).Where(segment => segment.Length > 0).ToList();
        if (segments.Count == 0)
        {
            throw new PythonIndexException(nameof(text), "list index out of range");
        }

        var match = DescriptorPattern.Match(segments[0].Replace('/', '\\'))
            ?? throw new PythonValueException("Registry root descriptor must start with HKLM\\ or HKCU\\", nameof(text));
        var hive = RegistryPython.Upper(match.Group(1)!);
        var path = PythonText.Strip(match.Group(2)!);
        if (path.Length == 0)
        {
            throw new PythonValueException("Registry root path segment must be non-empty", nameof(text));
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
            throw new PythonValueException($"Registry root option '{option}' must be formatted as key=value", nameof(option));
        }

        var key = PythonText.Lower(PythonText.Strip(option[..separator]));
        var rawValue = PythonText.Strip(option[(separator + 1)..]);
        if (!string.Equals(key, "view", StringComparison.Ordinal))
        {
            throw new PythonValueException($"Unsupported registry root option '{key}'", nameof(option));
        }

        if (rawValue.Length == 0)
        {
            throw new PythonValueException("Registry root view must be non-empty when provided", nameof(option));
        }

        return RegistryPython.Upper(rawValue) switch
        {
            "AUTO" => null,
            "32" => "32",
            "64" => "64",
            _ => throw new PythonValueException("Registry root view must be 32, 64, or auto", nameof(option)),
        };
    }
}
