using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects Dockerfiles using directive heuristics: the filename contains "dockerfile" or ends in ".dockerfile", the
/// first non-comment line starts with FROM, and common directives (RUN, COPY, ADD, ARG, ENV, WORKDIR, ENTRYPOINT,
/// CMD, EXPOSE, USER, VOLUME) appear at a line start.
/// </summary>
/// <remarks>
/// The plugin applies <c>^\s*FROM\s+\S+</c> (IGNORECASE, searched on the first non-comment line) and
/// <c>^\s*(RUN|COPY|...)\b</c> (IGNORECASE, MULTILINE, searched on the whole text). Both are matched by hand on code
/// points: Python's <c>\s</c> includes U+001C-U+001F, its <c>\b</c> derives from <c>\w</c> = [L N _], and its
/// IGNORECASE folds each ASCII letter to the set enumerated from the interpreter in <see cref="FoldsTo"/>
/// (I also matches U+0130 and U+0131, K matches U+212A, S matches U+017F), none of which .NET reproduces exactly.
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

    // re.IGNORECASE on a str pattern: an ASCII letter matches every code point whose simple lower case is the same
    // letter plus Python's fixed extra equivalences (i/dotless i, s/long s). Enumerated over all code points.
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

    // Python \b after a word character: the next code point is not [L N _], or the text ends.
    private static bool AtWordEnd(string text, int offset)
    {
        if (offset >= text.Length)
        {
            return true;
        }

        Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out _);
        return !EngineText.IsWordRune(rune);
    }

    // ^\s*FROM\s+\S+ (IGNORECASE, no MULTILINE) searched on one line: anchored at the start of the string.
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

    // ^\s*(RUN|COPY|...)\b (IGNORECASE, MULTILINE) searched over the text: at the start and after every "\n", skip
    // whitespace (which may cross further newlines) and test each alternative followed by a Python word boundary.
    // Each whitespace run is skipped once: every line start inside it reaches the same offset (see LineStartMatcher).
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
