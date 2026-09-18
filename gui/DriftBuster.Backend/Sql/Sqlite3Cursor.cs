using System.Runtime.InteropServices;
using System.Text;

using DriftBuster.Backend.Infrastructure;

using Microsoft.Data.Sqlite;

using SQLitePCL;

namespace DriftBuster.Backend.Sql;

/// <summary>
/// Runs one statement on an open database handle and returns every row, with the checks, the per-row value conversion and the
/// exception mapping the SQL export relies on. Written from the publicly documented SQLite C interface.
/// </summary>
internal static class Sqlite3Cursor
{
    // SQLITE_LIMIT_SQL_LENGTH.
    private const int LimitSqlLength = 1;

    // The decode-failure message is formatted into a 200-byte buffer with a size of 199, so at most 198 bytes survive.
    private const int DecodeMessageBytes = 198;

    /// <summary>
    /// The statement's column names (<c>cursor.description</c>) and every row it yields. Each value follows its per-row storage class:
    /// NULL is null, INTEGER a <see cref="long"/>, FLOAT a <see cref="double"/>, TEXT a string decoded as strict UTF-8, BLOB a byte array.
    /// </summary>
    /// <exception cref="SqliteException">SQLite refuses the statement or a step (its result code), the text holds a NUL or more than one
    /// statement (<c>SQLITE_MISUSE</c>), is longer than the library allows (<c>SQLITE_TOOBIG</c>), or a TEXT value is not UTF-8
    /// (<c>SQLITE_ERROR</c>).</exception>
    /// <exception cref="InvalidDataException">A column name or error message is not UTF-8.</exception>
    internal static Sqlite3Rows FetchAll(sqlite3 db, string sql)
    {
        var bytes = Encoding.UTF8.GetBytes(sql);
        if (bytes.Length > raw.sqlite3_limit(db, LimitSqlLength, -1))
        {
            throw new SqliteException("query string is too large", raw.SQLITE_TOOBIG);
        }

        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            throw new SqliteException("the query contains a null character", raw.SQLITE_MISUSE);
        }

        var terminated = new byte[bytes.Length + 1];
        bytes.CopyTo(terminated, 0);
        var rc = raw.sqlite3_prepare_v2(db, terminated, out var statement, out ReadOnlySpan<byte> tail);
        using (statement)
        {
            if (rc != raw.SQLITE_OK)
            {
                throw Error(db);
            }

            if (StatementFollows(tail))
            {
                throw new SqliteException("You can only execute one statement at a time.", raw.SQLITE_MISUSE);
            }

            return Drain(db, statement);
        }
    }

    /// <summary>
    /// <c>row[key]</c> on a <c>sqlite3.Row</c>: the first column whose name equals <paramref name="key"/>, or matches it ignoring ASCII case
    /// when both are pure ASCII; <see cref="KeyNotFoundException"/> otherwise.
    /// </summary>
    internal static object? Lookup(Sqlite3Rows result, object?[] row, string key)
    {
        for (var index = 0; index < result.Description.Count; index++)
        {
            if (NamesMatch(result.Description[index], key))
            {
                return index < row.Length ? row[index] : throw new InvalidOperationException("The row holds fewer values than the result has columns.");
            }
        }

        throw new KeyNotFoundException($"The row has no column named '{key}'.");
    }

    private static bool NamesMatch(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        return left.Length == right.Length
            && left.All(char.IsAscii)
            && right.All(char.IsAscii)
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static Sqlite3Rows Drain(sqlite3 db, sqlite3_stmt statement)
    {
        List<string>? description = null;
        var rows = new List<object?[]>();
        while (true)
        {
            var rc = raw.sqlite3_step(statement);
            if (rc != raw.SQLITE_ROW && rc != raw.SQLITE_DONE)
            {
                throw Error(db);
            }

            description ??= Describe(statement);
            if (rc == raw.SQLITE_DONE)
            {
                return new Sqlite3Rows(description, rows);
            }

            rows.Add(FetchRow(statement));
        }
    }

    private static List<string> Describe(sqlite3_stmt statement)
    {
        var names = new List<string>();
        var count = raw.sqlite3_column_count(statement);
        for (var index = 0; index < count; index++)
        {
            names.Add(EngineUtf8.Decode(NullTerminated(raw.sqlite3_column_name(statement, index))));
        }

        return names;
    }

    private static object?[] FetchRow(sqlite3_stmt statement)
    {
        var row = new object?[raw.sqlite3_data_count(statement)];
        for (var index = 0; index < row.Length; index++)
        {
            row[index] = raw.sqlite3_column_type(statement, index) switch
            {
                raw.SQLITE_NULL => null,
                raw.SQLITE_INTEGER => raw.sqlite3_column_int64(statement, index),
                raw.SQLITE_FLOAT => raw.sqlite3_column_double(statement, index),
                raw.SQLITE_TEXT => Text(statement, index),
                _ => raw.sqlite3_column_blob(statement, index).ToArray(),
            };
        }

        return row;
    }

    // sqlite3_column_text, then sqlite3_column_bytes (the order the library requires), decoded as strict UTF-8.
    private static string Text(sqlite3_stmt statement, int index)
    {
        var text = raw.sqlite3_column_text(statement, index);
        var bytes = Copy(text, raw.sqlite3_column_bytes(statement, index));
        try
        {
            return EngineUtf8.Decode(bytes);
        }
        catch (InvalidDataException)
        {
            var message = new List<byte>();
            message.AddRange("Could not decode to UTF-8 column '"u8.ToArray());
            message.AddRange(NullTerminated(raw.sqlite3_column_name(statement, index)));
            message.AddRange("' with text '"u8.ToArray());
            message.AddRange(bytes.TakeWhile(value => value != 0));
            message.Add((byte)'\'');
            var kept = message.Take(DecodeMessageBytes).Select(value => value < 0x80 ? (char)value : '\uFFFD');
            throw new SqliteException(string.Concat(kept), raw.SQLITE_ERROR);
        }
    }

    private static unsafe byte[] Copy(utf8z text, int length)
    {
        if (length <= 0)
        {
            return [];
        }

        fixed (byte* pointer = text)
        {
            return pointer == null ? [] : new ReadOnlySpan<byte>(pointer, length).ToArray();
        }
    }

    private static unsafe byte[] NullTerminated(utf8z text)
    {
        fixed (byte* pointer = text)
        {
            return pointer == null ? [] : MemoryMarshal.CreateReadOnlySpanFromNullTerminated(pointer).ToArray();
        }
    }

    /// <summary>
    /// The <see cref="SqliteException"/> for the handle's last error: its primary and extended result codes, and <c>sqlite3_errmsg</c>
    /// decoded as strict UTF-8.
    /// </summary>
    internal static Exception Error(sqlite3 db)
    {
        var code = raw.sqlite3_errcode(db) & 0xFF;
        if (code == raw.SQLITE_NOMEM)
        {
            return new InsufficientMemoryException();
        }

        var message = EngineUtf8.Decode(NullTerminated(raw.sqlite3_errmsg(db)));
        return new SqliteException(message, code, raw.sqlite3_extended_errcode(db));
    }

    // lstrip_sql: skips white space (space, tab, form feed, new line, carriage return), "--" line comments and "/* */" block comments;
    // true when a character other than those remains before the terminating NUL. An unterminated comment ends the text.
    private static bool StatementFollows(ReadOnlySpan<byte> tail)
    {
        var position = 0;
        byte At(ReadOnlySpan<byte> text, int index) => index < text.Length ? text[index] : (byte)0;
        while (At(tail, position) != 0)
        {
            switch (At(tail, position))
            {
                case (byte)' ' or (byte)'\t' or (byte)'\f' or (byte)'\n' or (byte)'\r':
                    position++;
                    break;
                case (byte)'-' when At(tail, position + 1) == '-':
                    position += 2;
                    while (At(tail, position) != 0 && At(tail, position) != '\n')
                    {
                        position++;
                    }

                    break;
                case (byte)'/' when At(tail, position + 1) == '*':
                    position += 2;
                    while (At(tail, position) != 0 && (At(tail, position) != '*' || At(tail, position + 1) != '/'))
                    {
                        position++;
                    }

                    if (At(tail, position) == 0)
                    {
                        return false;
                    }

                    position += 2;
                    break;
                default:
                    return true;
            }
        }

        return false;
    }
}
