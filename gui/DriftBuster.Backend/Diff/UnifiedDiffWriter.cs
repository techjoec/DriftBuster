using System.Globalization;

namespace DriftBuster.Backend.Diff;

/// <summary>
/// Standard unified diff text over the change regions of <see cref="LineDiff"/>: a <c>--- from</c> / <c>+++ to</c> header, then
/// hunks headed <c>@@ -range +range @@</c> whose lines start with a space (context), <c>-</c> (removed) or <c>+</c> (added).
/// Within a region every removed line precedes every added line. A hunk carries up to <c>contextLines</c> equal lines before
/// its first region and after its last, and regions separated by at most twice that many equal lines share a hunk.
/// </summary>
public static class UnifiedDiffWriter
{
    /// <summary>The unified diff of <paramref name="before"/> against <paramref name="after"/>; nothing when they are equal.</summary>
    public static IEnumerable<string> Lines(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after,
        string fromFile = "",
        string toFile = "",
        int contextLines = 3,
        string lineTerm = "\n")
        => Lines(before, after, LineDiff.Compare(before, after), fromFile, toFile, contextLines, lineTerm);

    /// <summary>
    /// The unified diff for <paramref name="changes"/> already computed by <see cref="LineDiff.Compare"/>. Every emitted line ends
    /// with <paramref name="lineTerm"/>; a negative <paramref name="contextLines"/> counts as zero.
    /// </summary>
    public static IEnumerable<string> Lines(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after,
        IReadOnlyList<LineChange> changes,
        string fromFile,
        string toFile,
        int contextLines,
        string lineTerm)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(changes);
        var output = new List<string>();
        if (changes.Count == 0)
        {
            return output;
        }

        output.Add("--- " + fromFile + lineTerm);
        output.Add("+++ " + toFile + lineTerm);
        long context = Math.Max(contextLines, 0);
        var first = 0;
        while (first < changes.Count)
        {
            var last = first;
            while (last + 1 < changes.Count && (long)changes[last + 1].BeforeStart - changes[last].BeforeEnd <= 2 * context)
            {
                last++;
            }

            WriteHunk(output, before, after, changes, first, last, context, lineTerm);
            first = last + 1;
        }

        return output;
    }

    /// <summary>
    /// The unified diff range of the 0-based half-open line range <c>[start, stop)</c>: <c>"{start+1}"</c> for one line,
    /// <c>"{start},0"</c> for none (the line before it), otherwise <c>"{start+1},{length}"</c>.
    /// </summary>
    public static string FormatRange(long start, long stop)
    {
        var length = stop - start;
        return length switch
        {
            1 => (start + 1).ToString(CultureInfo.InvariantCulture),
            0 => string.Create(CultureInfo.InvariantCulture, $"{start},0"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{start + 1},{length}"),
        };
    }

    private static void WriteHunk(
        List<string> output,
        IReadOnlyList<string> before,
        IReadOnlyList<string> after,
        IReadOnlyList<LineChange> changes,
        int first,
        int last,
        long context,
        string lineTerm)
    {
        var head = changes[first];
        var tail = changes[last];
        var leading = (int)Math.Min(context, head.BeforeStart);
        var trailing = (int)Math.Min(context, before.Count - tail.BeforeEnd);
        var beforeRange = FormatRange(head.BeforeStart - leading, tail.BeforeEnd + trailing);
        var afterRange = FormatRange(head.AfterStart - leading, tail.AfterEnd + trailing);
        output.Add($"@@ -{beforeRange} +{afterRange} @@{lineTerm}");

        var cursor = head.BeforeStart - leading;
        for (var index = first; index <= last; index++)
        {
            var change = changes[index];
            AddLines(output, " ", before, cursor, change.BeforeStart, lineTerm);
            AddLines(output, "-", before, change.BeforeStart, change.BeforeEnd, lineTerm);
            AddLines(output, "+", after, change.AfterStart, change.AfterEnd, lineTerm);
            cursor = change.BeforeEnd;
        }

        AddLines(output, " ", before, cursor, tail.BeforeEnd + trailing, lineTerm);
    }

    private static void AddLines(List<string> output, string prefix, IReadOnlyList<string> lines, int start, int stop, string lineTerm)
    {
        for (var index = start; index < stop; index++)
        {
            output.Add(prefix + lines[index] + lineTerm);
        }
    }
}
