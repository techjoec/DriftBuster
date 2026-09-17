namespace DriftBuster.Backend.Sql;

/// <summary>
/// A <c>sqlite3.Error</c> subclass raised by Python's <c>sqlite3</c> module: <see cref="TypeName"/> names the class
/// (<c>OperationalError</c>, <c>DatabaseError</c>, <c>ProgrammingError</c>, ...) and <see cref="Exception.Message"/> is <c>str(exc)</c>.
/// </summary>
public sealed class Sqlite3Exception : Exception
{
    public Sqlite3Exception()
        : this("DatabaseError", "SQLite error.")
    {
    }

    public Sqlite3Exception(string message)
        : this("DatabaseError", message)
    {
    }

    public Sqlite3Exception(string message, Exception innerException)
        : base(message, innerException)
    {
        TypeName = "DatabaseError";
    }

    public Sqlite3Exception(string typeName, string message, int? sqliteErrorCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        TypeName = typeName;
        SqliteErrorCode = sqliteErrorCode;
    }

    /// <summary>The Python class name, without the <c>sqlite3.</c> module prefix.</summary>
    public string TypeName { get; }

    /// <summary>
    /// <c>exc.sqlite_errorcode</c>: the extended result code of an error the SQLite library reported; null for the checks the module
    /// makes itself (<c>ProgrammingError</c>, <c>DataError</c>, the text decode failure).
    /// </summary>
    public int? SqliteErrorCode { get; }
}
