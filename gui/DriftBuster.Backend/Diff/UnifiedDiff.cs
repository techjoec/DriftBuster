using System.Globalization;

namespace DriftBuster.Backend.Diff;

/// <summary>CPython 3.13 <c>difflib.unified_diff</c> and the reporting module's <c>_calculate_stats</c>.</summary>
public static class UnifiedDiff
{
    /// <summary>
    /// <c>unified_diff(a, b, fromfile, tofile, fromfiledate, tofiledate, n, lineterm)</c>: nothing when the sequences
    /// are equal; otherwise the two headers (a date follows a tab only when given), then per hunk the range line and
    /// the context, removed and added lines with no terminator of their own.
    /// </summary>
    public static IEnumerable<string> Lines(
        IReadOnlyList<string> a,
        IReadOnlyList<string> b,
        string fromFile = "",
        string toFile = "",
        string fromFileDate = "",
        string toFileDate = "",
        int n = 3,
        string lineTerm = "\n")
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var started = false;
        foreach (var group in new SequenceMatcher(a, b).GetGroupedOpcodes(n))
        {
            if (!started)
            {
                started = true;
                var fromDate = fromFileDate.Length > 0 ? "\t" + fromFileDate : string.Empty;
                var toDate = toFileDate.Length > 0 ? "\t" + toFileDate : string.Empty;
                yield return $"--- {fromFile}{fromDate}{lineTerm}";
                yield return $"+++ {toFile}{toDate}{lineTerm}";
            }

            var first = group[0];
            var last = group[^1];
            yield return $"@@ -{FormatRange(first.I1, last.I2)} +{FormatRange(first.J1, last.J2)} @@{lineTerm}";
            foreach (var line in HunkLines(group, a, b))
            {
                yield return line;
            }
        }
    }

    private static IEnumerable<string> HunkLines(IReadOnlyList<DiffOpcode> group, IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        foreach (var (tag, i1, i2, j1, j2) in group)
        {
            if (tag == DiffOpcodeTag.Equal)
            {
                foreach (var line in Slice(a, i1, i2))
                {
                    yield return " " + line;
                }

                continue;
            }

            if (tag is DiffOpcodeTag.Replace or DiffOpcodeTag.Delete)
            {
                foreach (var line in Slice(a, i1, i2))
                {
                    yield return "-" + line;
                }
            }

            if (tag is DiffOpcodeTag.Replace or DiffOpcodeTag.Insert)
            {
                foreach (var line in Slice(b, j1, j2))
                {
                    yield return "+" + line;
                }
            }
        }
    }

    /// <summary><c>_format_range_unified(start, stop)</c>: "N" for one line, "N,0" (the line before) for none, else "N,len".</summary>
    public static string FormatRange(long start, long stop)
    {
        var beginning = start + 1;
        var length = stop - start;
        if (length == 1)
        {
            return beginning.ToString(CultureInfo.InvariantCulture);
        }

        if (length == 0)
        {
            beginning--;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{beginning},{length}");
    }

    /// <summary><c>seq[start:stop]</c> with Python's clamping and negative-index rules (step 1).</summary>
    internal static IEnumerable<string> Slice(IReadOnlyList<string> items, long start, long stop)
    {
        var count = items.Count;
        var from = (int)Math.Clamp(start < 0 ? start + count : start, 0, count);
        var to = (int)Math.Clamp(stop < 0 ? stop + count : stop, 0, count);
        for (var index = from; index < to; index++)
        {
            yield return items[index];
        }
    }

    /// <summary><c>_calculate_stats(before, after)</c>: inserted, deleted and the longer side of every replaced run.</summary>
    public static DiffStats CalculateStats(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var added = 0;
        var removed = 0;
        var changed = 0;
        foreach (var (tag, i1, i2, j1, j2) in new SequenceMatcher(before, after).GetOpcodes())
        {
            switch (tag)
            {
                case DiffOpcodeTag.Replace:
                    changed += (int)Math.Max(i2 - i1, j2 - j1);
                    break;
                case DiffOpcodeTag.Delete:
                    removed += (int)(i2 - i1);
                    break;
                case DiffOpcodeTag.Insert:
                    added += (int)(j2 - j1);
                    break;
                default:
                    break;
            }
        }

        return new DiffStats(added, removed, changed);
    }
}
