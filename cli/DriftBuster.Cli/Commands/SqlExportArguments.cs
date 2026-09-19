using System.CommandLine;

using DriftBuster.Backend.Remote;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Cli.Commands;

/// <summary>The export options both <c>driftbuster sql-export</c> and <c>driftbuster capture export-sql</c> declare, read into <see cref="SqlExportOptions"/>.</summary>
internal sealed class SqlExportArguments
{
    private readonly Argument<string[]> _database = new("database") { Arity = ArgumentArity.OneOrMore, Description = "Path(s) to SQLite databases." };
    private readonly Option<string> _outputDir = CliOptions.Text("--output-dir", "sql-exports", "Directory to store exported SQL snapshots.");
    private readonly Option<string[]> _table = CliOptions.Append("--table", "Restrict export to a specific table (repeatable).");
    private readonly Option<string[]> _excludeTable = CliOptions.Append("--exclude-table", "Exclude a specific table from export (repeatable).");
    private readonly Option<string[]> _maskColumn = CliOptions.Append("--mask-column", "Mask sensitive column data using placeholder (table.column).");
    private readonly Option<string[]> _hashColumn = CliOptions.Append("--hash-column", "Deterministically hash column data (table.column).");
    private readonly Option<string> _placeholder = CliOptions.Text("--placeholder", "[REDACTED]", "Placeholder used when masking columns.");
    private readonly Option<string> _hashSalt = CliOptions.Text("--hash-salt", string.Empty, "Salt applied when hashing column data.");
    private readonly Option<int?> _limit = new("--limit") { Description = "Optional maximum rows to export per table." };
    private readonly Option<string> _prefix = CliOptions.Text("--prefix", string.Empty, "Optional prefix to apply to exported snapshot filenames.");

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
        Databases = parseResult.GetValue(_database)!,
        OutputDir = parseResult.GetValue(_outputDir)!,
        Prefix = parseResult.GetValue(_prefix),
        Settings = new SqlExportSettings
        {
            Tables = parseResult.GetValue(_table)!,
            ExcludeTables = parseResult.GetValue(_excludeTable)!,
            MaskedColumns = SqlExportSettings.ParseColumns(parseResult.GetValue(_maskColumn)!),
            HashedColumns = SqlExportSettings.ParseColumns(parseResult.GetValue(_hashColumn)!),
            Placeholder = parseResult.GetValue(_placeholder)!,
            HashSalt = parseResult.GetValue(_hashSalt)!,
            Limit = parseResult.GetValue(_limit),
        },
    };
}
