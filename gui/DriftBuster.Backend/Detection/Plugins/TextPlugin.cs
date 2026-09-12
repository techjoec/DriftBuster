using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects directive-style text configuration files: whitespace-delimited directives without explicit '=' or ':'
/// separators (OpenSSH sshd_config, OpenVPN client.conf). Runs as the low-priority fallback after structured parsers.
/// </summary>
/// <remarks>
/// The Python plugin is regex driven (<c>^\s*[A-Za-z_][\w.-]*(?:\s+.+)?$</c>, <c>^\s*Subsystem\s+sftp\b</c>,
/// <c>^\s*client\s*$</c>, <c>^\s*(dev|remote|proto)\b</c>). Those are matched here by hand on code points because
/// .NET regexes differ on exactly the inputs that flip the outcome: <c>\s</c> excludes U+001C-U+001F, <c>\w</c> uses
/// [L Mn Nd Pc] instead of [L N _], and character classes see UTF-16 units so an astral letter ends a token.
/// </remarks>
public sealed class TextPlugin : IFormatPlugin
{
    private const int LineWindow = 500;
    private const int MarkerWindow = 100;
    private const int CountCap = 50;

    private static readonly string[] OpenvpnKeywords = ["dev", "remote", "proto"];

    public string Name => "text";

    public int Priority => 1000;

    public string Version => "0.0.2";

    private enum LineKind
    {
        Blank,
        Comment,
        Assignment,
        Directive,
        Other,
    }

    private static LineKind ClassifyLine(string line)
    {
        var s = PythonText.Strip(line);
        if (s.Length == 0)
        {
            return LineKind.Blank;
        }

        if (s[0] is '#' or ';')
        {
            return LineKind.Comment;
        }

        if (s.Contains('=', StringComparison.Ordinal) || s.Contains(':', StringComparison.Ordinal))
        {
            return LineKind.Assignment;
        }

        if (IsDirective(s))
        {
            return LineKind.Directive;
        }

        return LineKind.Other;
    }

    // ^[A-Za-z_][\w.-]*(?:\s+.+)?$ on a stripped line: the token runs over [L N _ . -] code points and is followed by
    // end of line or whitespace (a stripped line ends in a non-space, so "\s+.+" then always has something to match).
    private static bool IsDirective(string s)
    {
        if (!(char.IsAsciiLetter(s[0]) || s[0] == '_'))
        {
            return false;
        }

        var offset = 1;
        while (offset < s.Length)
        {
            Rune.DecodeFromUtf16(s.AsSpan(offset), out var rune, out var consumed);
            if (rune.Value is '.' or '-' || PythonText.IsWordRune(rune))
            {
                offset += consumed;
                continue;
            }

            return rune.Value <= char.MaxValue && PythonText.IsSpace((char)rune.Value);
        }

        return true;
    }

    // "keyword\b" where \b is Python's: the next code point is not [L N _] (or the line ends there).
    private static bool StartsWithWord(string s, int offset, string keyword)
    {
        if (!s.AsSpan(offset).StartsWith(keyword, StringComparison.Ordinal))
        {
            return false;
        }

        var next = offset + keyword.Length;
        if (next >= s.Length)
        {
            return true;
        }

        Rune.DecodeFromUtf16(s.AsSpan(next), out var rune, out _);
        return !PythonText.IsWordRune(rune);
    }

    private static int SkipSpaces(string s, int offset)
    {
        while (offset < s.Length && PythonText.IsSpace(s[offset]))
        {
            offset++;
        }

        return offset;
    }

    // ^\s*Subsystem\s+sftp\b with MULTILINE over "\n".join(lines). "\s+" may consume the joining newlines, so a
    // line that is "Subsystem" (plus trailing whitespace) followed by whitespace-only lines and then a line whose first
    // token is "sftp" matches too; the search stops at the end of the window because the join does.
    private static bool HasOpensshSubsystemMarker(List<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var offset = SkipSpaces(line, 0);
            if (!line.AsSpan(offset).StartsWith("Subsystem", StringComparison.Ordinal))
            {
                continue;
            }

            offset += "Subsystem".Length;
            var afterSpaces = SkipSpaces(line, offset);
            if (afterSpaces < line.Length)
            {
                if (afterSpaces > offset && StartsWithWord(line, afterSpaces, "sftp"))
                {
                    return true;
                }

                continue;
            }

            // The rest of the line is whitespace: the "\n" joining the next line satisfies "\s+", as do any
            // whitespace-only lines after it.
            var next = index + 1;
            while (next < lines.Count && SkipSpaces(lines[next], 0) == lines[next].Length)
            {
                next++;
            }

            if (next < lines.Count && StartsWithWord(lines[next], SkipSpaces(lines[next], 0), "sftp"))
            {
                return true;
            }
        }

        return false;
    }

    // ^\s*client\s*$ with MULTILINE: a line that is "client" once stripped.
    private static bool IsOpenvpnClientLine(string line) => string.Equals(PythonText.Strip(line), "client", StringComparison.Ordinal);

    // ^\s*(dev|remote|proto)\b with MULTILINE.
    private static bool IsOpenvpnDirectiveLine(string line)
    {
        var offset = SkipSpaces(line, 0);
        return OpenvpnKeywords.Any(keyword => StartsWithWord(line, offset, keyword));
    }

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (text is null)
        {
            return null;
        }

        var lines = TextLines.SplitLines(text).Take(LineWindow).ToList();
        var kinds = lines.Select(ClassifyLine).ToList();
        var directiveCount = kinds.Count(kind => kind == LineKind.Directive);
        var assignmentCount = kinds.Count(kind => kind == LineKind.Assignment);
        var commentCount = kinds.Count(kind => kind == LineKind.Comment);

        var lower = PathText.NameLower(path);

        // Known subtypes can be recognised with fewer directive lines.
        var opensshHint = string.Equals(lower, "sshd_config", StringComparison.Ordinal) || HasOpensshSubsystemMarker(lines);
        var openvpnHint = lines.Any(IsOpenvpnClientLine) && lines.Any(IsOpenvpnDirectiveLine);

        if ((directiveCount >= 4 && assignmentCount <= 1)
            || (opensshHint && directiveCount >= 3)
            || (openvpnHint && directiveCount >= 3))
        {
            return BuildMatch(lines, directiveCount, commentCount, opensshHint, openvpnHint);
        }

        return null;
    }

    private DetectionMatch BuildMatch(List<string> lines, int directiveCount, int commentCount, bool opensshHint, bool openvpnHint)
    {
        var reasons = new List<string> { "Detected whitespace-delimited directives with minimal assignments" };
        if (commentCount > 0)
        {
            reasons.Add("Found comment lines typical of text configs");
        }

        var variant = "generic-directive-text";
        if (opensshHint)
        {
            variant = "openssh-conf";
            reasons.Add("Matched OpenSSH markers (sshd_config or Subsystem sftp)");
        }
        else if (openvpnHint)
        {
            variant = "openvpn-conf";
            reasons.Add("Matched OpenVPN markers (client/dev/remote/proto)");
        }

        var confidence = 0.68;
        if (!string.Equals(variant, "generic-directive-text", StringComparison.Ordinal))
        {
            confidence += 0.12;
        }

        confidence = Math.Min(0.9, confidence);

        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["directive_lines"] = Math.Min(directiveCount, CountCap),
        };
        if (commentCount > 0)
        {
            metadata["comment_lines"] = Math.Min(commentCount, CountCap);
        }

        // Oddities: obvious nonstandard marker tokens.
        var reviewReasons = new List<string>();
        if (lines.Take(MarkerWindow).Any(line => PythonText.StripStart(line).StartsWith("<<<", StringComparison.Ordinal)))
        {
            reviewReasons.Add("Nonstandard marker tokens present (e.g., '<<<')");
        }

        if (reviewReasons.Count > 0)
        {
            metadata["needs_review"] = true;
            metadata["review_reasons"] = reviewReasons;
        }

        return new DetectionMatch(Name, "unix-conf", variant, confidence, reasons, metadata);
    }
}
