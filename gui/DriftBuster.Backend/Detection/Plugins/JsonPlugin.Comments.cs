using System.Text;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Comment stripping and detection outside string literals.</summary>
public sealed partial class JsonPlugin
{
    /// <summary>
    /// Drops leading U+FEFF characters, then any run of ASCII whitespace, <c>//</c> line comments and <c>/* */</c>
    /// block comments. An unterminated comment yields an empty string. The flag reports whether a comment was consumed.
    /// </summary>
    internal static (string Stripped, bool Consumed) StripLeadingComments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var working = text.TrimStart('\uFEFF');
        var consumed = false;
        var index = 0;
        var length = working.Length;
        while (true)
        {
            while (index < length && working[index] is ' ' or '\t' or '\r' or '\n')
            {
                index++;
            }

            if (index + 1 < length && working[index] == '/' && working[index + 1] == '/')
            {
                consumed = true;
                var newline = working.IndexOf('\n', index + 2);
                if (newline == -1)
                {
                    return (string.Empty, consumed);
                }

                index = newline + 1;
                continue;
            }

            if (index + 1 < length && working[index] == '/' && working[index + 1] == '*')
            {
                consumed = true;
                var end = working.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end == -1)
                {
                    return (string.Empty, consumed);
                }

                index = end + 2;
                continue;
            }

            break;
        }

        return (working[index..], consumed);
    }

    /// <summary>True when a <c>//</c> or <c>/*</c> token appears outside a double-quoted string literal.</summary>
    internal static bool ContainsComments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var inString = false;
        var escape = false;
        var length = text.Length;
        for (var i = 0; i < length; i++)
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

            if (ch == '/' && i + 1 < length && text[i + 1] is '/' or '*')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes <c>//</c> and <c>/* */</c> comments outside string literals. Returns the input untouched (and false)
    /// when no comment token is present or a block comment is unterminated.
    /// </summary>
    internal static (string Cleaned, bool Removed) StripJsonComments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains("//", StringComparison.Ordinal) && !text.Contains("/*", StringComparison.Ordinal))
        {
            return (text, false);
        }

        var result = new StringBuilder(text.Length);
        bool inString = false, escape = false, removed = false;
        var i = 0;

        while (i < text.Length)
        {
            var ch = text[i];
            if (inString)
            {
                result.Append(ch);
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

                i++;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '/')
            {
                var next = SkipComment(text, i);
                if (next < 0)
                {
                    return (text, false);
                }

                if (next > i)
                {
                    removed = true;
                    i = next;
                    continue;
                }
            }

            result.Append(ch);
            i++;
        }

        return (result.ToString(), removed);
    }

    // Index just past the comment starting at "start" ("/" already seen): the line end for "//", after "*/" for
    // "/*" (-1 when unterminated), or "start" itself when no comment starts here.
    private static int SkipComment(string text, int start)
    {
        if (start + 1 >= text.Length)
        {
            return start;
        }

        var next = text[start + 1];
        if (next == '/')
        {
            var i = start + 2;
            while (i < text.Length && text[i] is not ('\r' or '\n'))
            {
                i++;
            }

            return i;
        }

        if (next == '*')
        {
            var end = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
            return end == -1 ? -1 : end + 2;
        }

        return start;
    }
}
