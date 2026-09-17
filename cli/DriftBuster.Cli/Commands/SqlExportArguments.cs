using System.CommandLine;
using System.Numerics;

using DriftBuster.Backend.Remote;

namespace DriftBuster.Cli.Commands;

/// <summary>The export parser both <c>cli.py export-sql</c> and <c>capture.py export-sql</c> declare, read into <see cref="SqlExportOptions"/>.</summary>
internal sealed class SqlExportArguments
{
    private readonly Argument<string[]> _database = new("database") { Arity = ArgumentArity.OneOrMore, Description = "Path(s) to SQLite databases." };
    private readonly Option<string> _outputDir = PythonArguments.Text("--output-dir", "sql-exports", "Directory to store exported SQL snapshots.");
    private readonly Option<string[]> _table = PythonArguments.Append("--table", "Restrict export to a specific table (repeatable).");
    private readonly Option<string[]> _excludeTable = PythonArguments.Append("--exclude-table", "Exclude a specific table from export (repeatable).");
    private readonly Option<string[]> _maskColumn = PythonArguments.Append("--mask-column", "Mask sensitive column data using placeholder (table.column).");
    private readonly Option<string[]> _hashColumn = PythonArguments.Append("--hash-column", "Deterministically hash column data (table.column).");
    private readonly Option<string> _placeholder = PythonArguments.Text("--placeholder", "[REDACTED]", "Placeholder used when masking columns.");
    private readonly Option<string> _hashSalt = PythonArguments.Text("--hash-salt", string.Empty, "Salt applied when hashing column data.");
    private readonly Option<BigInteger?> _limit = PythonArguments.OptionalInt("--limit", "Optional maximum rows to export per table.");
    private readonly Option<string> _prefix = PythonArguments.Text("--prefix", string.Empty, "Optional prefix to apply to exported snapshot filenames.");

    public SqlExportArguments(Command command)
    {
        command.Arguments.Add(_database);
        foreach (var option in new Option[] { _outputDir, _table, _excludeTable, _maskColumn, _hashColumn, _placeholder, _hashSalt, _limit, _prefix })
        {
            command.Options.Add(option);
        }
    }

    public SqlExportOptions Read(ParseResult parseResult) => new()
    {
        Database = parseResult.GetValue(_database)!,
        OutputDir = parseResult.GetValue(_outputDir)!,
        Table = parseResult.GetValue(_table)!,
        ExcludeTable = parseResult.GetValue(_excludeTable)!,
        MaskColumn = parseResult.GetValue(_maskColumn)!,
        HashColumn = parseResult.GetValue(_hashColumn)!,
        Placeholder = parseResult.GetValue(_placeholder),
        HashSalt = parseResult.GetValue(_hashSalt),
        Limit = parseResult.GetValue(_limit),
        Prefix = parseResult.GetValue(_prefix),
    };
}
