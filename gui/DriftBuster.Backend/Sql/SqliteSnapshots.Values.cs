using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using DriftBuster.Backend.Diff;

namespace DriftBuster.Backend.Sql;

public static partial class SqliteSnapshots
{
    /// <summary>
    /// Bytes become <c>{"type": "base64", "value": ...}</c> (standard alphabet, padded); null, integers, floats and strings are returned
    /// as they are.
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
    /// <c>sha256:</c> plus the lower-case hex SHA-256 of the UTF-8 salt followed by the value as sorted, ASCII-escaped JSON (floats in
    /// shortest round-trip form, infinities as <c>Infinity</c>; bytes as the JSON string of <see cref="BytesRepr"/>).
    /// </summary>
    /// <remarks>
    /// An unpaired surrogate in <paramref name="salt"/> is encoded as U+FFFD; table and column names read from SQLite never hold one.
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
    /// <c>b'...'</c>, or <c>b"..."</c> when the bytes hold a single quote and no double quote; the quote in use and backslash are escaped,
    /// tab, new line and carriage return by name, every other byte outside space..~ as <c>\xhh</c>. Hashed values must keep this spelling.
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
