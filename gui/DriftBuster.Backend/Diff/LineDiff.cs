namespace DriftBuster.Backend.Diff;

/// <summary>
/// A minimal line diff: Myers' O(ND) greedy search ("An O(ND) Difference Algorithm and Its Variations", 1986) in its
/// linear-space form, which bisects each sub-problem at a middle snake found by running the search from both ends at once.
/// Lines are compared with ordinal string equality. The common prefix and suffix of every sub-problem are matched before
/// searching. When two candidate paths reach equally far on a diagonal the search extends the one that deletes a before
/// line, so the same inputs always yield the same regions.
/// </summary>
public static class LineDiff
{
    /// <summary>The change regions, in order: maximal runs of removed and inserted lines between runs of equal lines.</summary>
    public static IReadOnlyList<LineChange> Compare(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var left = Encode(before, ids);
        var right = Encode(after, ids);
        var removed = new bool[left.Length];
        var inserted = new bool[right.Length];
        new Search(left, right, removed, inserted).Run();
        return CollectRegions(removed, inserted);
    }

    /// <summary>Counts over <see cref="Compare"/>: see <see cref="CalculateStats(IReadOnlyList{LineChange})"/>.</summary>
    public static DiffStats CalculateStats(IReadOnlyList<string> before, IReadOnlyList<string> after)
        => CalculateStats(Compare(before, after));

    /// <summary>
    /// Regions that only insert add to <see cref="DiffStats.AddedLines"/>, regions that only remove add to
    /// <see cref="DiffStats.RemovedLines"/>, and a region that does both adds the larger of its two sides to
    /// <see cref="DiffStats.ChangedLines"/>.
    /// </summary>
    public static DiffStats CalculateStats(IReadOnlyList<LineChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        int added = 0, removed = 0, changed = 0;
        foreach (var change in changes)
        {
            if (change.Removed == 0)
            {
                added += change.Inserted;
            }
            else if (change.Inserted == 0)
            {
                removed += change.Removed;
            }
            else
            {
                changed += Math.Max(change.Removed, change.Inserted);
            }
        }

        return new DiffStats(added, removed, changed);
    }

    private static int[] Encode(IReadOnlyList<string> lines, Dictionary<string, int> ids)
    {
        var codes = new int[lines.Count];
        for (var index = 0; index < codes.Length; index++)
        {
            if (!ids.TryGetValue(lines[index], out var id))
            {
                id = ids.Count;
                ids.Add(lines[index], id);
            }

            codes[index] = id;
        }

        return codes;
    }

    // Unmarked lines on both sides pair up in order as the common subsequence, so a region ends where both sides next hold an
    // unmarked line.
    private static List<LineChange> CollectRegions(bool[] removed, bool[] inserted)
    {
        var regions = new List<LineChange>();
        int i = 0, j = 0;
        while (i < removed.Length || j < inserted.Length)
        {
            if (i < removed.Length && j < inserted.Length && !removed[i] && !inserted[j])
            {
                i++;
                j++;
                continue;
            }

            int startI = i, startJ = j;
            while (i < removed.Length && removed[i])
            {
                i++;
            }

            while (j < inserted.Length && inserted[j])
            {
                j++;
            }

            regions.Add(new LineChange(startI, i, startJ, j));
        }

        return regions;
    }

    private sealed class Search(int[] left, int[] right, bool[] removed, bool[] inserted)
    {
        // Furthest x reached per diagonal, forward from the top-left and backward from the bottom-right (as a distance from the
        // sub-problem's end). Indexed by diagonal + offset; sized for the whole problem and reused by every sub-problem.
        private readonly int[] _forward = new int[(2 * (left.Length + right.Length)) + 5];
        private readonly int[] _backward = new int[(2 * (left.Length + right.Length)) + 5];
        private readonly int _offset = left.Length + right.Length + 2;

        public void Run()
        {
            var pending = new Stack<(int ALo, int AHi, int BLo, int BHi)>();
            pending.Push((0, left.Length, 0, right.Length));
            while (pending.Count > 0)
            {
                var (aLo, aHi, bLo, bHi) = pending.Pop();
                while (aLo < aHi && bLo < bHi && left[aLo] == right[bLo])
                {
                    aLo++;
                    bLo++;
                }

                while (aLo < aHi && bLo < bHi && left[aHi - 1] == right[bHi - 1])
                {
                    aHi--;
                    bHi--;
                }

                if (aLo == aHi)
                {
                    Array.Fill(inserted, value: true, bLo, bHi - bLo);
                }
                else if (bLo == bHi)
                {
                    Array.Fill(removed, value: true, aLo, aHi - aLo);
                }
                else
                {
                    var (startX, startY, endX, endY) = MiddleSnake(aLo, aHi, bLo, bHi);
                    pending.Push((endX, aHi, endY, bHi));
                    pending.Push((aLo, startX, bLo, startY));
                }
            }
        }

        // The snake (absolute start and end points) in the middle of a shortest edit path for a sub-problem whose first and last
        // lines differ on both sides, so it needs at least two edits and both halves it leaves are strictly smaller.
        private (int StartX, int StartY, int EndX, int EndY) MiddleSnake(int aLo, int aHi, int bLo, int bHi)
        {
            var n = aHi - aLo;
            var m = bHi - bLo;
            var delta = n - m;
            var oddDelta = (delta & 1) != 0;
            var limit = ((n + m + 1) / 2) + 1;
            _forward[_offset + 1] = 0;
            _backward[_offset + 1] = 0;
            for (var d = 0; d < limit; d++)
            {
                for (var k = -d; k <= d; k += 2)
                {
                    var x = k == -d || (k != d && _forward[_offset + k - 1] < _forward[_offset + k + 1])
                        ? _forward[_offset + k + 1]
                        : _forward[_offset + k - 1] + 1;
                    var y = x - k;
                    int x0 = x, y0 = y;
                    while (x < n && y < m && left[aLo + x] == right[bLo + y])
                    {
                        x++;
                        y++;
                    }

                    _forward[_offset + k] = x;
                    var reverse = delta - k;
                    if (oddDelta && reverse >= -(d - 1) && reverse <= d - 1 && x + _backward[_offset + reverse] >= n)
                    {
                        return (aLo + x0, bLo + y0, aLo + x, bLo + y);
                    }
                }

                for (var k = -d; k <= d; k += 2)
                {
                    var x = k == -d || (k != d && _backward[_offset + k - 1] < _backward[_offset + k + 1])
                        ? _backward[_offset + k + 1]
                        : _backward[_offset + k - 1] + 1;
                    var y = x - k;
                    int x0 = x, y0 = y;
                    while (x < n && y < m && left[aHi - 1 - x] == right[bHi - 1 - y])
                    {
                        x++;
                        y++;
                    }

                    _backward[_offset + k] = x;
                    var forward = delta - k;
                    if (!oddDelta && forward >= -d && forward <= d && x + _forward[_offset + forward] >= n)
                    {
                        return (aHi - x, bHi - y, aHi - x0, bHi - y0);
                    }
                }
            }

            throw new InvalidOperationException("The line diff search did not meet in the middle.");
        }
    }
}
