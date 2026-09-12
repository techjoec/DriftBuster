namespace DriftBuster.Backend.Infrastructure;

/// <summary>Line splitting with the exact boundary set of Python <c>str.splitlines</c>.</summary>
public static class TextLines
{
    /// <summary>
    /// Splits <paramref name="text"/> on \n, \r, \r\n, \v, \f, \x1c, \x1d, \x1e, \x85, \u2028 and \u2029.
    /// A trailing line break does not produce an empty final element; an empty string yields no lines.
    /// </summary>
    public static IReadOnlyList<string> SplitLines(string text, bool keepEnds = false)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = new List<string>();
        var start = 0;
        var index = 0;
        var length = text.Length;

        while (index < length)
        {
            var ch = text[index];
            if (!IsLineBoundary(ch))
            {
                index++;
                continue;
            }

            var end = index;
            index++;
            if (ch == '\r' && index < length && text[index] == '\n')
            {
                index++;
            }

            lines.Add(keepEnds ? text[start..index] : text[start..end]);
            start = index;
        }

        if (start < length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    public static bool IsLineBoundary(char ch) => ch switch
    {
        '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029' => true,
        _ => false,
    };
}
