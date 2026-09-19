using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects Dockerfiles: filename containing "dockerfile" or ending ".dockerfile", a first non-comment line starting with FROM,
/// and common directives (RUN, COPY, ADD, ARG, ENV, WORKDIR, ENTRYPOINT, CMD, EXPOSE, USER, VOLUME) at line starts.
/// </summary>
/// <remarks>
/// <c>^\s*FROM\s+\S+</c> and <c>^\s*(RUN|COPY|...)\b</c> are case-insensitive regexes; the directive pattern runs as a <c>\G</c>
/// regex through <see cref="LineStartMatcher"/>.
/// </remarks>
public sealed partial class DockerfilePlugin : IFormatPlugin
{
    [GeneratedRegex(@"^\s*FROM\s+\S", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex FirstFromPattern { get; }

    [GeneratedRegex(@"\G\s*(?:RUN|COPY|ADD|ARG|ENV|WORKDIR|ENTRYPOINT|CMD|EXPOSE|USER|VOLUME)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    internal static partial Regex DirectivePattern { get; }

    public string Name => "dockerfile";

    public int Priority => 120;

    public string Version => "0.0.1";

    // ^\s*FROM\s+\S+ on one line, case-insensitive.
    internal static bool HasFirstFrom(string line) => FirstFromPattern.IsMatch(line);

    // ^\s*(RUN|COPY|...)\b over the text, case-insensitive.
    internal static bool HasDirectives(string text) => LineStartMatcher.IsMatch(DirectivePattern, text);

    private static string FirstNonCommentLine(string text)
    {
        var lines = TextLines.SplitLines(text);
        var index = 0;
        while (index < lines.Count
            && (lines[index].Trim().Length == 0 || lines[index].TrimStart().StartsWith('#')))
        {
            index++;
        }

        return index < lines.Count ? lines[index] : string.Empty;
    }

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (text is null)
        {
            return null;
        }

        var lower = PathText.NameLower(path);
        var reasons = new List<string>();

        var nameHint = lower.Contains("dockerfile", StringComparison.Ordinal) || lower.EndsWith(".dockerfile", StringComparison.Ordinal);
        if (nameHint)
        {
            reasons.Add("Filename suggests a Dockerfile");
        }

        var hasFrom = HasFirstFrom(FirstNonCommentLine(text));
        if (hasFrom)
        {
            reasons.Add("First non-comment line starts with FROM");
        }

        var hasDirectives = HasDirectives(text);
        if (hasDirectives)
        {
            reasons.Add("Found common Dockerfile directives (RUN/COPY/ARG)");
        }

        var signals = new[] { nameHint, hasFrom, hasDirectives }.Count(flag => flag);
        if (signals < 2)
        {
            return null;
        }

        var confidence = 0.6;
        if (nameHint)
        {
            confidence += 0.1;
        }

        if (hasFrom)
        {
            confidence += 0.15;
        }

        if (hasDirectives)
        {
            confidence += 0.1;
        }

        confidence = Math.Min(0.95, confidence);

        if (reasons.Count == 0)
        {
            reasons.Add("Dockerfile heuristics matched");
        }

        // The plugin records no metadata.
        return new DetectionMatch(Name, "dockerfile", "generic", confidence, reasons, metadata: null);
    }
}
