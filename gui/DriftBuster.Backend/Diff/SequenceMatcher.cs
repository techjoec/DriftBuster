namespace DriftBuster.Backend.Diff;

/// <summary>
/// CPython 3.13 <c>difflib.SequenceMatcher(None, a, b, autojunk)</c> over lists of <c>str</c>: elements compare by
/// ordinal equality (a Python <c>str</c> equals another exactly when their code points do, which is exactly when their
/// UTF-16 units do).
/// </summary>
/// <remarks>
/// <c>__chain_b</c> indexes every position of each element of <c>b</c>; with <c>autojunk</c> and <c>len(b) &gt;= 200</c>
/// an element occurring more than <c>len(b) // 100 + 1</c> times is popular and dropped from the index (it is not junk:
/// <c>isjunk</c> is None, so <c>bjunk</c> stays empty and the extension loops of <c>find_longest_match</c> still cross
/// popular elements). Tie-breaking, the recursion order of <c>get_matching_blocks</c> (a LIFO queue), the collapse of
/// adjacent blocks and <c>get_grouped_opcodes</c> follow the CPython source line for line. Python caches the opcode list
/// and <c>get_grouped_opcodes</c> rewrites its first and last entries in place; here every call returns a fresh list.
/// </remarks>
public sealed class SequenceMatcher
{
    private const int AutojunkMinimumLength = 200;

    private readonly IReadOnlyList<string> _a;
    private readonly IReadOnlyList<string> _b;
    private readonly Dictionary<string, List<int>> _b2j = new(StringComparer.Ordinal);
    private readonly HashSet<string> _popular = new(StringComparer.Ordinal);

    public SequenceMatcher(IReadOnlyList<string> a, IReadOnlyList<string> b, bool autojunk = true)
    {
        _a = a ?? throw new ArgumentNullException(nameof(a));
        _b = b ?? throw new ArgumentNullException(nameof(b));
        ChainB(autojunk);
    }

    /// <summary><c>bpopular</c>: the elements autojunk removed from the index.</summary>
    public IReadOnlySet<string> Popular => _popular;

    private void ChainB(bool autojunk)
    {
        for (var index = 0; index < _b.Count; index++)
        {
            var element = _b[index];
            if (!_b2j.TryGetValue(element, out var indices))
            {
                indices = [];
                _b2j[element] = indices;
            }

            indices.Add(index);
        }

        var n = _b.Count;
        if (!autojunk || n < AutojunkMinimumLength)
        {
            return;
        }

        var ntest = (n / 100) + 1;
        foreach (var (element, indices) in _b2j)
        {
            if (indices.Count > ntest)
            {
                _popular.Add(element);
            }
        }

        foreach (var element in _popular)
        {
            _b2j.Remove(element);
        }
    }

    /// <summary><c>find_longest_match(alo, ahi, blo, bhi)</c>.</summary>
    public MatchingBlock FindLongestMatch(int alo, int ahi, int blo, int bhi)
    {
        var bestI = alo;
        var bestJ = blo;
        var bestSize = 0;
        var j2len = new Dictionary<int, int>();
        var newJ2len = new Dictionary<int, int>();
        for (var i = alo; i < ahi; i++)
        {
            newJ2len.Clear();
            if (_b2j.TryGetValue(_a[i], out var indices))
            {
                foreach (var j in indices)
                {
                    if (j < blo)
                    {
                        continue;
                    }

                    if (j >= bhi)
                    {
                        break;
                    }

                    var k = (j2len.TryGetValue(j - 1, out var previous) ? previous : 0) + 1;
                    newJ2len[j] = k;
                    if (k > bestSize)
                    {
                        (bestI, bestJ, bestSize) = (i - k + 1, j - k + 1, k);
                    }
                }
            }

            (j2len, newJ2len) = (newJ2len, j2len);
        }

        // bjunk is empty (isjunk is None): the non-junk extension crosses every equal element, popular ones included,
        // and the junk extension that follows in CPython never advances.
        while (bestI > alo && bestJ > blo && string.Equals(_a[bestI - 1], _b[bestJ - 1], StringComparison.Ordinal))
        {
            (bestI, bestJ, bestSize) = (bestI - 1, bestJ - 1, bestSize + 1);
        }

        while (bestI + bestSize < ahi && bestJ + bestSize < bhi
            && string.Equals(_a[bestI + bestSize], _b[bestJ + bestSize], StringComparison.Ordinal))
        {
            bestSize++;
        }

        return new MatchingBlock(bestI, bestJ, bestSize);
    }

    /// <summary><c>get_matching_blocks()</c>, ending with the <c>(len(a), len(b), 0)</c> sentinel.</summary>
    public IReadOnlyList<MatchingBlock> GetMatchingBlocks()
    {
        var la = _a.Count;
        var lb = _b.Count;
        var queue = new List<(int Alo, int Ahi, int Blo, int Bhi)> { (0, la, 0, lb) };
        var blocks = new List<MatchingBlock>();
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue[^1];
            queue.RemoveAt(queue.Count - 1);
            var match = FindLongestMatch(alo, ahi, blo, bhi);
            var (i, j, k) = (match.A, match.B, match.Size);
            if (k == 0)
            {
                continue;
            }

            blocks.Add(match);
            if (alo < i && blo < j)
            {
                queue.Add((alo, i, blo, j));
            }

            if (i + k < ahi && j + k < bhi)
            {
                queue.Add((i + k, ahi, j + k, bhi));
            }
        }

        // Blocks never share an (a, b) start, so ordering by the first two fields is the tuple sort.
        blocks.Sort((x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B != y.B ? x.B.CompareTo(y.B) : x.Size.CompareTo(y.Size));
        return CollapseAdjacent(blocks, la, lb);
    }

    private static List<MatchingBlock> CollapseAdjacent(List<MatchingBlock> blocks, int la, int lb)
    {
        var (i1, j1, k1) = (0, 0, 0);
        var nonAdjacent = new List<MatchingBlock>();
        foreach (var (i2, j2, k2) in blocks)
        {
            if (i1 + k1 == i2 && j1 + k1 == j2)
            {
                k1 += k2;
            }
            else
            {
                if (k1 != 0)
                {
                    nonAdjacent.Add(new MatchingBlock(i1, j1, k1));
                }

                (i1, j1, k1) = (i2, j2, k2);
            }
        }

        if (k1 != 0)
        {
            nonAdjacent.Add(new MatchingBlock(i1, j1, k1));
        }

        nonAdjacent.Add(new MatchingBlock(la, lb, 0));
        return nonAdjacent;
    }

    /// <summary><c>get_opcodes()</c>.</summary>
    public IReadOnlyList<DiffOpcode> GetOpcodes()
    {
        var i = 0;
        var j = 0;
        var answer = new List<DiffOpcode>();
        foreach (var (ai, bj, size) in GetMatchingBlocks())
        {
            if (i < ai && j < bj)
            {
                answer.Add(new DiffOpcode(DiffOpcodeTag.Replace, i, ai, j, bj));
            }
            else if (i < ai)
            {
                answer.Add(new DiffOpcode(DiffOpcodeTag.Delete, i, ai, j, bj));
            }
            else if (j < bj)
            {
                answer.Add(new DiffOpcode(DiffOpcodeTag.Insert, i, ai, j, bj));
            }

            (i, j) = (ai + size, bj + size);
            if (size != 0)
            {
                answer.Add(new DiffOpcode(DiffOpcodeTag.Equal, ai, i, bj, j));
            }
        }

        return answer;
    }

    /// <summary>
    /// <c>get_grouped_opcodes(n)</c>: change hunks with up to <paramref name="n"/> lines of context. Every sum is taken
    /// in 64 bits, as Python's unbounded integers take it: <c>n + n</c>, <c>i1 + n</c> and <c>i2 - n</c> overflow 32 bits
    /// once <c>|n|</c> reaches 2^30.
    /// </summary>
    public IEnumerable<IReadOnlyList<DiffOpcode>> GetGroupedOpcodes(int n = 3)
    {
        long context = n;
        var codes = GetOpcodes().ToList();
        if (codes.Count == 0)
        {
            codes.Add(new DiffOpcode(DiffOpcodeTag.Equal, 0, 1, 0, 1));
        }

        // Fix up leading and trailing groups that show no changes (one tuple may be both).
        if (codes[0].Tag == DiffOpcodeTag.Equal)
        {
            var (tag, i1, i2, j1, j2) = codes[0];
            codes[0] = new DiffOpcode(tag, Math.Max(i1, i2 - context), i2, Math.Max(j1, j2 - context), j2);
        }

        if (codes[^1].Tag == DiffOpcodeTag.Equal)
        {
            var (tag, i1, i2, j1, j2) = codes[^1];
            codes[^1] = new DiffOpcode(tag, i1, Math.Min(i2, i1 + context), j1, Math.Min(j2, j1 + context));
        }

        var nn = context + context;
        var group = new List<DiffOpcode>();
        foreach (var code in codes)
        {
            var (tag, i1, i2, j1, j2) = code;
            if (tag == DiffOpcodeTag.Equal && i2 - i1 > nn)
            {
                group.Add(new DiffOpcode(tag, i1, Math.Min(i2, i1 + context), j1, Math.Min(j2, j1 + context)));
                yield return group;
                group = [];
                (i1, j1) = (Math.Max(i1, i2 - context), Math.Max(j1, j2 - context));
            }

            group.Add(new DiffOpcode(tag, i1, i2, j1, j2));
        }

        if (group.Count > 0 && !(group.Count == 1 && group[0].Tag == DiffOpcodeTag.Equal))
        {
            yield return group;
        }
    }
}
