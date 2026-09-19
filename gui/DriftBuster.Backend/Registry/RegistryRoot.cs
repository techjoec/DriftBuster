using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// A hive (<c>HKLM</c> or <c>HKCU</c>), a key path and an optional view (<c>"32"</c>, <c>"64"</c>, null for default); equality by all
/// three, ordinal.
/// </summary>
public sealed partial record RegistryRoot(string Hive, string Path, string? View = null)
{
    [GeneratedRegex(
        @"\A(?<hive>HKLM|HKCU)\\(?<path>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DescriptorPattern();

    public (string Hive, string Path, string? View) AsTuple() => (Hive, Path, View);

    /// <summary>
    /// Parses <c>HIVE\path[,view=32|64|auto]</c>: "/" read as "\", hive case-insensitive and upper-cased, path trimmed, <c>view=auto</c>
    /// is no view (the last view option wins).
    /// </summary>
    /// <exception cref="FormatException">A malformed descriptor, including one of only commas and whitespace.</exception>
    public static RegistryRoot Parse(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new FormatException("Registry root descriptor must be non-empty");
        }

        var segments = value.Split(',').Select(item => item.Trim()).Where(segment => segment.Length > 0).ToList();
        if (segments.Count == 0)
        {
            throw new FormatException("Registry root descriptor must be non-empty");
        }

        var match = DescriptorPattern().Match(segments[0].Replace('/', '\\'));
        if (!match.Success)
        {
            throw new FormatException("Registry root descriptor must start with HKLM\\ or HKCU\\");
        }

        var hive = RegistryText.Upper(match.Groups["hive"].Value);
        var path = match.Groups["path"].Value.Trim();
        if (path.Length == 0)
        {
            throw new FormatException("Registry root path segment must be non-empty");
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
            throw new FormatException($"Registry root option '{option}' must be formatted as key=value");
        }

        var key = option[..separator].Trim().ToLowerInvariant();
        var rawValue = (option[(separator + 1)..]).Trim();
        if (!string.Equals(key, "view", StringComparison.Ordinal))
        {
            throw new FormatException($"Unsupported registry root option '{key}'");
        }

        if (rawValue.Length == 0)
        {
            throw new FormatException("Registry root view must be non-empty when provided");
        }

        return RegistryText.Upper(rawValue) switch
        {
            "AUTO" => null,
            "32" => "32",
            "64" => "64",
            _ => throw new FormatException("Registry root view must be 32, 64, or auto"),
        };
    }
}
