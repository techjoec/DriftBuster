using System.CommandLine;
using System.Numerics;

using DriftBuster.Backend.Remote;

namespace DriftBuster.Cli.Commands;

/// <summary>The export options both <c>driftbuster sql-export</c> and <c>driftbuster capture export-sql</c> declare, read into <see cref="SqlExportOptions"/>.</summary>
internal sealed class SqlExportArguments
{
    private readonly Argument<string[]> _database = new("database") { Arity = ArgumentArity.OneOrMore, Description = "Path(s) to SQLite databases." };
    private readonly Option<string> _outputDir = EngineArguments.Text("--output-dir", "sql-exports", "Directory to store exported SQL snapshots.");
    private readonly Option<string[]> _table = EngineArguments.Append("--table", "Restrict export to a specific table (repeatable).");
    private readonly Option<string[]> _excludeTable = EngineArguments.Append("--exclude-table", "Exclude a specific table from export (repeatable).");
    private readonly Option<string[]> _maskColumn = EngineArguments.Append("--mask-column", "Mask sensitive column data using placeholder (table.column).");
    private readonly Option<string[]> _hashColumn = EngineArguments.Append("--hash-column", "Deterministically hash column data (table.column).");
    private readonly Option<string> _placeholder = EngineArguments.Text("--placeholder", "[REDACTED]", "Placeholder used when masking columns.");
    private readonly Option<string> _hashSalt = EngineArguments.Text("--hash-salt", string.Empty, "Salt applied when hashing column data.");
    private readonly Option<BigInteger?> _limit = EngineArguments.OptionalInt("--limit", "Optional maximum rows to export per table.");
    private readonly Option<string> _prefix = EngineArguments.Text("--prefix", string.Empty, "Optional prefix to apply to exported snapshot filenames.");

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
