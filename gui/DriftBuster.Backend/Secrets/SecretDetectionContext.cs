using DriftBuster.Backend.Infrastructure.EngineRe;

namespace DriftBuster.Backend.Secrets;

/// <summary>The rules and ignore lists for one copy run, plus its findings.</summary>
public sealed class SecretDetectionContext(
    IReadOnlyList<SecretDetectionRule> rules,
    string version,
    IReadOnlySet<string> ignoreRules,
    IReadOnlyList<EnginePattern> ignorePatterns,
    IReadOnlyList<string> ignorePatternText,
    bool rulesLoaded)
{
    public IReadOnlyList<SecretDetectionRule> Rules { get; } = rules;

    public string Version { get; } = version;

    public IReadOnlySet<string> IgnoreRules { get; } = ignoreRules;

    /// <summary>Ignore patterns that compiled (no flags); searched against the original line.</summary>
    public IReadOnlyList<EnginePattern> IgnorePatterns { get; } = ignorePatterns;

    /// <summary>Every distinct ignore pattern given, compiled or not.</summary>
    public IReadOnlyList<string> IgnorePatternText { get; } = ignorePatternText;

    /// <summary><c>findings</c>: appended to by every redaction.</summary>
    public IList<SecretFinding> Findings { get; } = new List<SecretFinding>();

    /// <summary>Each rule stopped on a line where redaction would otherwise never finish.</summary>
    public IList<SecretRedactionGuard> RedactionGuards { get; } = new List<SecretRedactionGuard>();

    public bool RulesLoaded { get; } = rulesLoaded;
}
