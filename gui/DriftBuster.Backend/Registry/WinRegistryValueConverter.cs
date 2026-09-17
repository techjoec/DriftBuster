using System.Numerics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// <c>RegToValue</c> from CPython 3.13's <c>PC/winreg.c</c>: the Python value <c>winreg.EnumValue</c> returns for a value's raw data and
/// type, following that file (PSF License) branch for branch.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>REG_DWORD</c> and <c>REG_QWORD</c>: the little-endian unsigned integer (0 for no data). Data shorter than the integer is
/// read zero-padded, where CPython reads past the data into its buffer.</item>
/// <item><c>REG_SZ</c> and <c>REG_EXPAND_SZ</c>: the UTF-16 units up to the first NUL (a trailing odd byte ignored), unexpanded.</item>
/// <item><c>REG_MULTI_SZ</c>: an empty list for no data; otherwise the units, less one trailing NUL, split at every NUL, each string
/// ending at the next NUL or the end of the data, so an empty string between two NULs is kept.</item>
/// <item>Every other type (<c>REG_BINARY</c>, <c>REG_NONE</c>, <c>REG_DWORD_BIG_ENDIAN</c>, <c>REG_LINK</c>, ...): the bytes, or
/// null for no data.</item>
/// </list>
/// Integers are narrowed as <see cref="EngineValues.Narrow"/> narrows them, lists are <see cref="List{T}"/> of <see cref="object"/>
/// and bytes are <see cref="byte"/> arrays.
/// </remarks>
public static class WinRegistryValueConverter
{
    public const int RegNone = 0;
    public const int RegSz = 1;
    public const int RegExpandSz = 2;
    public const int RegBinary = 3;
    public const int RegDword = 4;
    public const int RegDwordBigEndian = 5;
    public const int RegLink = 6;
    public const int RegMultiSz = 7;
    public const int RegQword = 11;

    /// <summary>The Python value for the raw <paramref name="data"/> of registry type <paramref name="type"/>.</summary>
    public static object? Convert(ReadOnlySpan<byte> data, int type)
        => type switch
        {
            RegDword => EngineValues.Narrow(ReadUnsigned(data, 4)),
            RegQword => EngineValues.Narrow(ReadUnsigned(data, 8)),
            RegSz or RegExpandSz => UnitsUntilNul(Units(data), 0, data.Length / 2),
            RegMultiSz => MultiString(data),
            _ => data.Length == 0 ? null : data.ToArray(),
        };

    private static BigInteger ReadUnsigned(ReadOnlySpan<byte> data, int width)
    {
        var buffer = new byte[width];
        data[..Math.Min(width, data.Length)].CopyTo(buffer);
        return new BigInteger(buffer, isUnsigned: true, isBigEndian: false);
    }

    private static char[] Units(ReadOnlySpan<byte> data)
    {
        var units = new char[data.Length / 2];
        for (var index = 0; index < units.Length; index++)
        {
            units[index] = (char)(data[index * 2] | (data[(index * 2) + 1] << 8));
        }

        return units;
    }

    // PyUnicode_FromWideChar(start, wcsnlen(start, limit)).
    private static string UnitsUntilNul(char[] units, int start, int limit)
    {
        var end = start;
        while (end - start < limit && end < units.Length && units[end] != '\0')
        {
            end++;
        }

        return new string(units, start, end - start);
    }

    // countStrings, fixupMultiSZ and the REG_MULTI_SZ loop of RegToValue.
    private static List<object?> MultiString(ReadOnlySpan<byte> data)
    {
        var result = new List<object?>();
        if (data.Length == 0)
        {
            return result;
        }

        var units = Units(data);
        var len = units.Length;
        var stop = len > 0 && units[len - 1] == '\0' ? len - 1 : len;
        var starts = new List<int>();
        for (var position = 0; position < stop; position++)
        {
            starts.Add(position);
            while (position < stop && units[position] != '\0')
            {
                position++;
            }
        }

        foreach (var start in starts)
        {
            var text = UnitsUntilNul(units, start, len);
            result.Add(text);
            len -= text.Length + 1;
        }

        return result;
    }
}
