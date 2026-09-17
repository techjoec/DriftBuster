using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Hunt;

/// <summary>How to locate one kind of dynamic configuration value.</summary>
public sealed class HuntRule
{
    /// <summary>
    /// String patterns are .NET regular expressions built by <see cref="PatternRegex.Create"/> with
    /// <see cref="RegexOptions.IgnoreCase"/> and <see cref="RegexOptions.Multiline"/>; keywords are lowered; the token name is
    /// stripped and an empty one becomes null.
    /// </summary>
    public HuntRule(string name, string description, string? tokenName = null, IEnumerable<string>? keywords = null, IEnumerable<string>? patterns = null)
        : this(name, description, tokenName, keywords, (patterns ?? []).Select(pattern => PatternRegex.Create(pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline)).ToList())
    {
    }

    /// <summary>A rule over already compiled patterns, which are used as given.</summary>
    public HuntRule(string name, string description, string? tokenName, IEnumerable<string>? keywords, IReadOnlyList<Regex> compiledPatterns)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(compiledPatterns);
        Name = name;
        Description = description;
        Keywords = (keywords ?? []).Select(EngineText.Lower).ToList();
        Patterns = compiledPatterns;
        if (tokenName is not null)
        {
            var normalised = EngineText.Strip(tokenName);
            TokenName = normalised.Length == 0 ? null : normalised;
        }
    }

    public string Name { get; }

    public string Description { get; }

    public string? TokenName { get; }

    /// <summary>Lowered keywords; every one must appear in a file and at least one in a line.</summary>
    public IReadOnlyList<string> Keywords { get; }

    public IReadOnlyList<Regex> Patterns { get; }
}
