using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects Dockerfiles: filename containing "dockerfile" or ending ".dockerfile", a first non-comment line starting with FROM,
/// and common directives (RUN, COPY, ADD, ARG, ENV, WORKDIR, ENTRYPOINT, CMD, EXPOSE, USER, VOLUME) at line starts.
/// </summary>
/// <remarks>
/// <c>^\s*FROM\s+\S+</c> and <c>^\s*(RUN|COPY|...)\b</c> (case-insensitive) are matched by hand on code points: whitespace
/// includes U+001C-U+001F, <c>\b</c> uses [L N _], and case folding adds U+0130/U+0131 for I, U+212A for K, U+017F for S
/// (<see cref="FoldsTo"/>).
/// </remarks>
public sealed class DockerfilePlugin : IFormatPlugin
{
    private static readonly string[] Directives =
    [
        "RUN", "COPY", "ADD", "ARG", "ENV", "WORKDIR", "ENTRYPOINT", "CMD", "EXPOSE", "USER", "VOLUME",
    ];

    public string Name => "dockerfile";

    public int Priority => 120;

    public string Version => "0.0.1";

    // Case-insensitive ASCII letter match with the extra equivalents listed in the class remarks.
    private static bool FoldsTo(char upper, char actual)
    {
        if (actual == upper || actual == (char)(upper + 32))
        {
            return true;
        }

        return upper switch
        {
            'I' => actual is 'İ' or 'ı',
            'K' => actual == 'K',
            'S' => actual == 'ſ',
            _ => false,
        };
    }

    private static bool StartsWithFolded(string text, int offset, string keyword)
    {
        if (offset + keyword.Length > text.Length)
        {
            return false;
        }

        for (var index = 0; index < keyword.Length; index++)
        {
            if (!FoldsTo(keyword[index], text[offset + index]))
            {
                return false;
            }
        }

        return true;
    }

    private static int SkipSpaces(string text, int offset)
    {
        while (offset < text.Length && EngineText.IsSpace(text[offset]))
        {
            offset++;
        }

        return offset;
    }

    // Word boundary after a word character: the next code point is not [L N _], or the text ends.
    private static bool AtWordEnd(string text, int offset)
    {
        if (offset >= text.Length)
        {
            return true;
        }

        Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out _);
        return !EngineText.IsWordRune(rune);
    }

    // ^\s*FROM\s+\S+ on one line, case-insensitive.
    internal static bool HasFirstFrom(string line)
    {
        var offset = SkipSpaces(line, 0);
        if (!StartsWithFolded(line, offset, "FROM"))
        {
            return false;
        }

        offset += "FROM".Length;
        var afterSpaces = SkipSpaces(line, offset);
        return afterSpaces > offset && afterSpaces < line.Length;
    }

    // ^\s*(RUN|COPY|...)\b over the text, case-insensitive; whitespace runs are skipped once as in LineStartMatcher.
    internal static bool HasDirectives(string text)
    {
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var offset = SkipSpaces(text, lineStart);
            foreach (var directive in Directives)
            {
                if (StartsWithFolded(text, offset, directive) && AtWordEnd(text, offset + directive.Length))
                {
                    return true;
                }
            }

            lineStart = LineStartMatcher.NextLineStart(text, offset + 1);
        }

        return false;
    }

    private static string FirstNonCommentLine(string text)
    {
        var lines = TextLines.SplitLines(text);
        var index = 0;
        while (index < lines.Count
            && (EngineText.Strip(lines[index]).Length == 0 || EngineText.StripStart(lines[index]).StartsWith('#')))
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
