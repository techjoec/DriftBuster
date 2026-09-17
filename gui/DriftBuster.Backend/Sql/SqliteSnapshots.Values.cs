using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using DriftBuster.Backend.Diff;

namespace DriftBuster.Backend.Sql;

public static partial class SqliteSnapshots
{
    /// <summary>
    /// <c>_normalise_value(value)</c> over the values <c>sqlite3</c> returns: bytes become <c>{"type": "base64", "value": ...}</c>
    /// (standard alphabet, padded); None, int, float and str are returned as they are.
    /// </summary>
    internal static object? NormaliseValue(object? value) => value switch
    {
        byte[] bytes => new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "base64",
            ["value"] = Convert.ToBase64String(bytes),
        },
        _ => value,
    };

    /// <summary>
    /// <c>_hash_text(value, salt=salt)</c>: <c>"sha256:" + sha256(salt.encode("utf-8") + json.dumps(value, sort_keys=True,
    /// default=str).encode("utf-8")).hexdigest()</c>. The JSON is ASCII-escaped, floats use <c>repr</c> (infinities as
    /// <c>Infinity</c>), and bytes are dumped as the JSON string of their <c>repr</c> (<c>"b'...'"</c>).
    /// </summary>
    /// <remarks>
    /// An unpaired surrogate in <paramref name="salt"/> is encoded as U+FFFD, where Python's strict encode raises (the operator decision
    /// recorded for hashes in "Python raises on unpaired surrogates"); table and column names read from SQLite never hold one.
    /// </remarks>
    internal static string HashText(object? value, string salt)
    {
        ArgumentNullException.ThrowIfNull(salt);
        var text = Canonicaliser.Dumps(value is byte[] bytes ? BytesRepr(bytes) : value, indent: false, ensureAscii: true, sortKeys: true);
        var saltBytes = Encoding.UTF8.GetBytes(salt);
        var textBytes = Encoding.UTF8.GetBytes(text);
        var digest = SHA256.HashData([.. saltBytes, .. textBytes]);
        return "sha256:" + Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// <c>repr(bytes)</c>: <c>b'...'</c>, or <c>b"..."</c> when the bytes hold a single quote and no double quote; the quote in use and
    /// backslash are escaped, tab, new line and carriage return by name, and every other byte outside space to "~" as <c>\xhh</c>.
    /// </summary>
    internal static string BytesRepr(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var quote = bytes.Contains((byte)'\'') && !bytes.Contains((byte)'"') ? '"' : '\'';
        var builder = new StringBuilder("b").Append(quote);
        foreach (var value in bytes)
        {
            _ = value switch
            {
                _ when value == quote || value == '\\' => builder.Append('\\').Append((char)value),
                (byte)'\t' => builder.Append("\\t"),
                (byte)'\n' => builder.Append("\\n"),
                (byte)'\r' => builder.Append("\\r"),
                < 0x20 or >= 0x7F => builder.Append("\\x").Append(value.ToString("x2", CultureInfo.InvariantCulture)),
                _ => builder.Append((char)value),
            };
        }

        return builder.Append(quote).ToString();
    }
}
