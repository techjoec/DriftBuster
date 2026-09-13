namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary>An immutable set of Unicode code points (0 to U+10FFFF, surrogates included) held as sorted, disjoint, non-adjacent ranges.</summary>
internal sealed class CodePointSet
{
    public const int MaxCodePoint = 0x10FFFF;

    private readonly (int Low, int High)[] _ranges;

    private CodePointSet((int Low, int High)[] ranges)
    {
        _ranges = ranges;
    }

    public static CodePointSet Empty { get; } = new([]);

    public static CodePointSet All { get; } = new([(0, MaxCodePoint)]);

    public IReadOnlyList<(int Low, int High)> Ranges => _ranges;

    public bool IsEmpty => _ranges.Length == 0;

    public static CodePointSet Single(int codePoint) => new([(codePoint, codePoint)]);

    public static CodePointSet Range(int low, int high) => low > high ? Empty : new([(low, high)]);

    /// <summary>Builds a set from arbitrary (possibly overlapping, unsorted) ranges.</summary>
    public static CodePointSet FromRanges(IEnumerable<(int Low, int High)> ranges)
    {
        var sorted = ranges.Where(range => range.Low <= range.High).OrderBy(range => range.Low).ToList();
        var merged = new List<(int Low, int High)>(sorted.Count);
        foreach (var range in sorted)
        {
            if (merged.Count > 0 && range.Low <= merged[^1].High + 1)
            {
                var last = merged[^1];
                merged[^1] = (last.Low, Math.Max(last.High, range.High));
            }
            else
            {
                merged.Add(range);
            }
        }

        return new CodePointSet([.. merged]);
    }

    public static CodePointSet FromCodePoints(IEnumerable<int> codePoints) => FromRanges(codePoints.Select(code => (code, code)));

    /// <summary>Every code point for which <paramref name="predicate"/> holds, scanned over the whole code space.</summary>
    public static CodePointSet FromPredicate(Func<int, bool> predicate)
    {
        var ranges = new List<(int Low, int High)>();
        var start = -1;
        for (var code = 0; code <= MaxCodePoint; code++)
        {
            if (predicate(code))
            {
                if (start < 0)
                {
                    start = code;
                }
            }
            else if (start >= 0)
            {
                ranges.Add((start, code - 1));
                start = -1;
            }
        }

        if (start >= 0)
        {
            ranges.Add((start, MaxCodePoint));
        }

        return new CodePointSet([.. ranges]);
    }

    public bool Contains(int codePoint)
    {
        var low = 0;
        var high = _ranges.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            var range = _ranges[middle];
            if (codePoint < range.Low)
            {
                high = middle - 1;
            }
            else if (codePoint > range.High)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    public CodePointSet Union(CodePointSet other) => FromRanges(_ranges.Concat(other._ranges));

    public CodePointSet Complement()
    {
        var ranges = new List<(int Low, int High)>(_ranges.Length + 1);
        var next = 0;
        foreach (var (low, high) in _ranges)
        {
            if (low > next)
            {
                ranges.Add((next, low - 1));
            }

            next = high + 1;
        }

        if (next <= MaxCodePoint)
        {
            ranges.Add((next, MaxCodePoint));
        }

        return new CodePointSet([.. ranges]);
    }

    public CodePointSet Intersect(CodePointSet other) => Complement().Union(other.Complement()).Complement();

    public CodePointSet Except(CodePointSet other) => Intersect(other.Complement());

    public IEnumerable<int> CodePoints()
    {
        foreach (var (low, high) in _ranges)
        {
            for (var code = low; code <= high; code++)
            {
                yield return code;
            }
        }
    }

    public bool SetEquals(CodePointSet other) => _ranges.AsSpan().SequenceEqual(other._ranges);
}
