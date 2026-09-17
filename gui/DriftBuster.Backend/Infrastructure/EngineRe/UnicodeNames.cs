using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary>
/// <c>unicodedata.lookup</c> as CPython 3.13 implements it (<c>_getcode</c>), for the <c>\N{name}</c> escape: Hangul syllable
/// and CJK unified ideograph names computed, every other character name and name alias read from the embedded
/// <c>Resources/unicode_names.txt.gz</c> (CPython's Unicode 15.1 tables, each entry checked against <c>unicodedata.lookup</c>).
/// </summary>
/// <remarks>
/// The name table lookup upper-cases ASCII letters of the query (<c>Py_TOUPPER</c>) and nothing else; the Hangul and CJK
/// prefixes are compared as written. A query longer than <c>NAME_MAXLEN</c> (256 UTF-8 bytes) is refused. Named sequences
/// resolve to several code points, which <c>re</c> refuses, so they are not in the table.
/// </remarks>
internal static class UnicodeNames
{
    private const string Resource = "DriftBuster.Backend.Resources.unicode_names.txt.gz";
    private const int NameMaxLength = 256;
    private const string HangulPrefix = "HANGUL SYLLABLE ";
    private const string CjkPrefix = "CJK UNIFIED IDEOGRAPH-";
    private const int SBase = 0xAC00;

    // unicodedata.c hangul_syllables: the L, V and T jamo name columns.
    private static readonly string[] LeadNames = ["G", "GG", "N", "D", "DD", "R", "M", "B", "BB", "S", "SS", "", "J", "JJ", "C", "K", "T", "P", "H"];
    private static readonly string[] VowelNames = ["A", "AE", "YA", "YAE", "EO", "E", "YEO", "YE", "O", "WA", "WAE", "OE", "YO", "U", "WEO", "WE", "WI", "YU", "EU", "YI", "I"];
    private static readonly string[] TrailNames = ["", "G", "GG", "GS", "N", "NJ", "NH", "D", "L", "LG", "LM", "LB", "LS", "LT", "LP", "LH", "M", "B", "BS", "S", "SS", "NG", "J", "C", "K", "T", "P", "H"];

    private static readonly Lazy<Dictionary<string, int>> Table = new(LoadTable);

    /// <summary>The code point named <paramref name="name"/>, or null where <c>unicodedata.lookup</c> raises <c>KeyError</c>.</summary>
    public static int? Lookup(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) > NameMaxLength)
        {
            return null;
        }

        if (name.StartsWith(HangulPrefix, StringComparison.Ordinal))
        {
            return HangulSyllable(name[HangulPrefix.Length..]);
        }

        if (name.StartsWith(CjkPrefix, StringComparison.Ordinal))
        {
            return UnifiedIdeograph(name[CjkPrefix.Length..]);
        }

        var key = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            key.Append(ch is >= 'a' and <= 'z' ? (char)(ch - 32) : ch);
        }

        return Table.Value.TryGetValue(key.ToString(), out var code) ? code : null;
    }

    private static int? HangulSyllable(string rest)
    {
        var position = 0;
        var lead = FindSyllable(rest, ref position, LeadNames);
        var vowel = FindSyllable(rest, ref position, VowelNames);
        var trail = FindSyllable(rest, ref position, TrailNames);
        if (lead < 0 || vowel < 0 || trail < 0 || position != rest.Length)
        {
            return null;
        }

        return SBase + (((lead * VowelNames.Length) + vowel) * TrailNames.Length) + trail;
    }

    // find_syllable: the longest column entry that prefixes the rest of the name; -1 when none does.
    private static int FindSyllable(string text, ref int position, string[] column)
    {
        var length = -1;
        var found = -1;
        for (var index = 0; index < column.Length; index++)
        {
            var candidate = column[index];
            if (candidate.Length <= length)
            {
                continue;
            }

            if (string.CompareOrdinal(text, position, candidate, 0, candidate.Length) == 0 && position + candidate.Length <= text.Length)
            {
                length = candidate.Length;
                found = index;
            }
        }

        position += Math.Max(length, 0);
        return found;
    }

    private static int? UnifiedIdeograph(string digits)
    {
        if (digits.Length is not (4 or 5) || digits.Any(ch => ch is not ((>= '0' and <= '9') or (>= 'A' and <= 'F'))))
        {
            return null;
        }

        var code = Convert.ToInt32(digits, 16);
        return IsUnifiedIdeograph(code) ? code : null;
    }

    // unicodedata.c is_unified_ideograph.
    private static bool IsUnifiedIdeograph(int code) => code is (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF)
        or (>= 0x20000 and <= 0x2A6DF) or (>= 0x2A700 and <= 0x2B739) or (>= 0x2B740 and <= 0x2B81D)
        or (>= 0x2B820 and <= 0x2CEA1) or (>= 0x2CEB0 and <= 0x2EBE0) or (>= 0x2EBF0 and <= 0x2EE5D)
        or (>= 0x30000 and <= 0x3134A) or (>= 0x31350 and <= 0x323AF);

    private static Dictionary<string, int> LoadTable()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"embedded resource {Resource} is missing");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, new UTF8Encoding(false, true));
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var separator = line.IndexOf(';', StringComparison.Ordinal);
            table[line[(separator + 1)..]] = Convert.ToInt32(line[..separator], 16);
        }

        return table;
    }
}
