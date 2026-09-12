using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Structural scanners and filename heuristics.</summary>
public sealed partial class JsonPlugin
{
    /// <summary>Number of code points in <paramref name="text"/>, matching Python <c>len(str)</c>.</summary>
    private static int CodePointCount(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>The UTF-16 length of the first <paramref name="codePoints"/> code points, matching <c>text[:n]</c>.</summary>
    private static int PrefixLength(string text, int codePoints)
    {
        var offset = 0;
        var seen = 0;
        while (offset < text.Length && seen < codePoints)
        {
            Rune.DecodeFromUtf16(text.AsSpan(offset), out _, out var consumed);
            offset += consumed;
            seen++;
        }

        return offset;
    }

    private static (string Text, bool Truncated) PrepareAnalysisWindow(string text)
    {
        var prefix = PrefixLength(text, AnalysisWindowClamp);
        return prefix >= text.Length ? (text, false) : (text[..prefix], true);
    }

    /// <summary>True when a ':' appears at brace depth exactly one, outside strings, within the first 10,000 code points.</summary>
    internal static bool HasKeyValueMarker(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var depth = 0;
        var inString = false;
        var escape = false;
        var limit = PrefixLength(text, KeyValueMarkerWindow);
        for (var i = 0; i < limit; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (ch == '\\')
                {
                    escape = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                depth++;
                continue;
            }

            if (ch == '}')
            {
                if (depth > 0)
                {
                    depth--;
                }

                continue;
            }

            if (depth == 1 && ch == ':')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the brace and bracket counts each differ by at most one.</summary>
    internal static bool BalancedPairs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var span = text.AsSpan();
        var openBraces = span.Count('{');
        var closeBraces = span.Count('}');
        var openBrackets = span.Count('[');
        var closeBrackets = span.Count(']');
        return Math.Abs(openBraces - closeBraces) <= 1 && Math.Abs(openBrackets - closeBrackets) <= 1;
    }

    /// <summary>"filename" for appsettings-style names, "content" for quoted ConnectionStrings/Logging keys, else null.</summary>
    internal static string? IsStructuredSettings(string filename, string text)
    {
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(text);
        if (StructuredFilenames.Contains(filename) || filename.StartsWith("appsettings.", StringComparison.Ordinal))
        {
            return "filename";
        }

        if (text.Contains("\"ConnectionStrings\"", StringComparison.Ordinal) || text.Contains("\"Logging\"", StringComparison.Ordinal))
        {
            return "content";
        }

        return null;
    }

    /// <summary>The environment segment(s) of an <c>appsettings.&lt;env&gt;.json</c> name (already lowered), or null.</summary>
    internal static string? ExtractAppsettingsEnvironment(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        if (!filename.StartsWith("appsettings.", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = filename.Split('.');
        if (parts.Length <= 2)
        {
            return null;
        }

        var environment = PythonText.Strip(string.Join('.', parts, 1, parts.Length - 2));
        return environment.Length == 0 ? null : environment;
    }

    /// <summary>
    /// The prefix up to the last position where every brace and bracket opened outside strings has been closed,
    /// stripped of surrounding whitespace.
    /// </summary>
    internal static string TruncateToStructuralBoundary(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var depthCurly = 0;
        var depthSquare = 0;
        var inString = false;
        var escape = false;
        var lastValid = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (ch == '\\')
                {
                    escape = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            switch (ch)
            {
                case '{':
                    depthCurly++;
                    break;
                case '}' when depthCurly > 0:
                    depthCurly--;
                    break;
                case '[':
                    depthSquare++;
                    break;
                case ']' when depthSquare > 0:
                    depthSquare--;
                    break;
            }

            // Both halves of a surrogate pair sit outside strings at the same depth, so the boundary never splits one.
            if (depthCurly == 0 && depthSquare == 0)
            {
                lastValid = index + 1;
            }
        }

        return PythonText.Strip(text[..lastValid]);
    }
}
