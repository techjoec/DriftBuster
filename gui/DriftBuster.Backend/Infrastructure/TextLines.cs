namespace DriftBuster.Backend.Infrastructure;

/// <summary>Line splitting on the line boundaries .NET recognises (<see cref="MemoryExtensions.EnumerateLines(ReadOnlySpan{char})"/>).</summary>
public static class TextLines
{
    /// <summary>
    /// Splits <paramref name="text"/> on CR LF, CR, LF, FF, NEL, LS and PS. A trailing line break does not produce an empty final
    /// element; an empty string yields no lines.
    /// </summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            lines.Add(line.ToString());
        }

        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
