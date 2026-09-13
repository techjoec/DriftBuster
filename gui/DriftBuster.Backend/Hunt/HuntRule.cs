using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Hunt;

/// <summary><c>driftbuster.hunt.HuntRule</c>: how to locate one kind of dynamic configuration value.</summary>
public sealed class HuntRule
{
    /// <summary>
    /// String patterns compile with <c>re.IGNORECASE | re.MULTILINE</c>; keywords are lowered with <c>str.lower()</c>;
    /// the token name is stripped and an empty one becomes null.
    /// </summary>
    public HuntRule(string name, string description, string? tokenName = null, IEnumerable<string>? keywords = null, IEnumerable<string>? patterns = null)
        : this(name, description, tokenName, keywords, (patterns ?? []).Select(pattern => PythonPattern.Compile(pattern, PythonReFlags.IgnoreCase | PythonReFlags.Multiline)).ToList())
    {
    }

    /// <summary>A rule over already compiled patterns, which are used as given (Python keeps <c>re.Pattern</c> items).</summary>
    public HuntRule(string name, string description, string? tokenName, IEnumerable<string>? keywords, IReadOnlyList<PythonPattern> compiledPatterns)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(compiledPatterns);
        Name = name;
        Description = description;
        Keywords = (keywords ?? []).Select(PythonText.Lower).ToList();
        Patterns = compiledPatterns;
        if (tokenName is not null)
        {
            var normalised = PythonText.Strip(tokenName);
            TokenName = normalised.Length == 0 ? null : normalised;
        }
    }

    public string Name { get; }

    public string Description { get; }

    public string? TokenName { get; }

    /// <summary>Lowered keywords; every one must appear in a file and at least one in a line.</summary>
    public IReadOnlyList<string> Keywords { get; }

    public IReadOnlyList<PythonPattern> Patterns { get; }
}
