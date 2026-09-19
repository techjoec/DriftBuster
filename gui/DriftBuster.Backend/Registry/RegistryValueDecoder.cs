using System.Buffers.Binary;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// Turns the raw data of a registry value into the value DriftBuster reports, following the value types Microsoft documents for
/// <c>RegEnumValueW</c>. Integers are unsigned little-endian <see cref="ulong"/> values; strings are
/// UTF-16LE code units kept as they are (unpaired surrogates included, environment references not expanded); every other type is
/// a copy of its bytes.
/// </summary>
public static class RegistryValueDecoder
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

    /// <summary>
    /// The value for <paramref name="data"/> of registry type <paramref name="type"/>:
    /// <list type="bullet">
    /// <item><c>REG_DWORD</c>/<c>REG_QWORD</c>: the leading 4 or 8 bytes (zero-padded when shorter) as an unsigned integer.</item>
    /// <item><c>REG_SZ</c>/<c>REG_EXPAND_SZ</c>: the text up to the first NUL (<c>""</c> for no data).</item>
    /// <item><c>REG_MULTI_SZ</c>: the NUL-separated strings, without the terminating empty entries.</item>
    /// <item>Anything else: a <see cref="byte"/> array, or null for no data.</item>
    /// </list>
    /// A trailing odd byte of string data is ignored.
    /// </summary>
    public static object? Convert(ReadOnlySpan<byte> data, int type) => type switch
    {
        RegDword => (ulong)BinaryPrimitives.ReadUInt32LittleEndian(Padded(data, sizeof(uint))),
        RegQword => BinaryPrimitives.ReadUInt64LittleEndian(Padded(data, sizeof(ulong))),
        RegSz or RegExpandSz => FirstString(Units(data)),
        RegMultiSz => StringList(Units(data)),
        _ => data.IsEmpty ? null : data.ToArray(),
    };

    private static ReadOnlySpan<byte> Padded(ReadOnlySpan<byte> data, int width)
    {
        if (data.Length >= width)
        {
            return data[..width];
        }

        var buffer = new byte[width];
        data.CopyTo(buffer);
        return buffer;
    }

    private static char[] Units(ReadOnlySpan<byte> data)
    {
        var units = new char[data.Length / 2];
        for (var index = 0; index < units.Length; index++)
        {
            units[index] = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(index * 2, 2));
        }

        return units;
    }

    private static string FirstString(char[] units)
    {
        var end = Array.IndexOf(units, '\0');
        return new string(units, 0, end < 0 ? units.Length : end);
    }

    private static List<string> StringList(char[] units)
    {
        var result = new List<string>();
        if (units.Length == 0)
        {
            return result;
        }

        var start = 0;
        for (var index = 0; index <= units.Length; index++)
        {
            if (index == units.Length || units[index] == '\0')
            {
                result.Add(new string(units, start, index - start));
                start = index + 1;
            }
        }

        // Data ending in NUL leaves one empty entry after that terminator; an empty entry left at the end is the list terminator.
        if (units[^1] == '\0')
        {
            result.RemoveAt(result.Count - 1);
            if (result.Count > 0 && result[^1].Length == 0)
            {
                result.RemoveAt(result.Count - 1);
            }
        }

        return result;
    }
}
