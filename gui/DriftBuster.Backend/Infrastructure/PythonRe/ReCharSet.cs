namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary>
/// A <see cref="CodePointSet"/> with a bitmap over the Basic Multilingual Plane in front of the range search, for the
/// matcher's hot path (the role <c>CHARSET</c> and <c>BIGCHARSET</c> play in <c>_sre</c>).
/// </summary>
internal sealed class ReCharSet
{
    private const int BmpSize = 0x10000;

    private readonly ulong[] _bmp = new ulong[BmpSize / 64];

    public ReCharSet(CodePointSet set)
    {
        Set = set;
        foreach (var (low, high) in set.Ranges)
        {
            if (low >= BmpSize)
            {
                break;
            }

            for (var code = low; code <= Math.Min(high, BmpSize - 1); code++)
            {
                _bmp[code >> 6] |= 1UL << (code & 63);
            }
        }
    }

    public CodePointSet Set { get; }

    public bool Contains(int code) => code < BmpSize ? (_bmp[code >> 6] & (1UL << (code & 63))) != 0 : Set.Contains(code);

    /// <summary>The length of the leading run of <paramref name="text"/> inside the set.</summary>
    public int CountRun(ReadOnlySpan<int> text)
    {
        var index = 0;
        while (index < text.Length && Contains(text[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>The index of the last code point of <paramref name="text"/> inside the set, or -1.</summary>
    public int LastIndexIn(ReadOnlySpan<int> text)
    {
        for (var index = text.Length - 1; index >= 0; index--)
        {
            if (Contains(text[index]))
            {
                return index;
            }
        }

        return -1;
    }
}
