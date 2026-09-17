using System.Text;
using System.Text.Json;

using DriftBuster.Cli.Commands;

using Microsoft.Data.Sqlite;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// Mirror of tests/cli/test_cli.py through <c>driftbuster scan|diff|sql-export</c> (<c>python -m driftbuster.cli</c>,
/// <c>cli diff</c>, <c>cli export-sql</c> and <c>driftbuster-export-sql</c>).
/// </summary>
public sealed class CliTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-cli-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine([_tmp.FullName, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Utf8);
        return path;
    }

    [Fact]
    public void CliTableOutput()
    {
        var sample = Write("configs/appsettings.json", """{"Logging": {"LogLevel": {"Default": "Information"}}}""");

        var run = CliInvocation.Invoke("scan", Path.GetDirectoryName(sample)!);

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Should().Contain("Path").And.Contain("appsettings.json").And.Contain("json").And.Contain("Severity").And.Contain("Severity hint")
            .And.Contain("medium").And.Contain("JSON configuration files");
    }

    [Fact]
    public void CliJsonOutput()
    {
        var sample = Write("config.json", """{"key": "value"}""");

        var run = CliInvocation.Invoke("scan", sample, "--json");

        run.ExitCode.Should().Be(0, run.Err);
        var payload = run.JsonLines();
        payload.Should().NotBeEmpty();
        payload[0].GetProperty("format").GetString().Should().Be("json");
        payload[0].GetProperty("severity").GetString().Should().Be("medium");
        payload[0].GetProperty("metadata").GetProperty("catalog_severity").GetString().Should().Be("medium");
        var hint = payload[0].GetProperty("severity_hint");
        hint.ValueKind.Should().Be(JsonValueKind.String);
        hint.GetString().Should().Contain("JSON configuration files");
    }

    [Fact]
    public void CliReportsMissingPath()
    {
        var run = CliInvocation.Invoke("scan", "/path/does/not/exist");

        run.ExitCode.Should().Be(2);
        run.Err.Should().Contain("Path does not exist");
    }

    [Fact]
    public void RelativePathFallsBackWhenOutsideRoot()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "root")).FullName;
        var outside = Write("other/config.json", "{}");

        var result = ScanCommand.RelativePath(root, outside);

        result.Should().Be(outside.Replace(Path.DirectorySeparatorChar, '/'));
    }

    /// <summary>Python stubs the parser so <c>parser.error</c> returns; the command itself returns 2 with the message.</summary>
    [Fact]
    public void MainReturnsCodeWhenParserErrorIsSuppressed()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = ScanCommand.Execute("/missing/path", "**/*", sampleSize: null, json: false, stdout, stderr);

        exitCode.Should().Be(2);
        stderr.ToString().Should().StartWith("driftbuster: error: Path does not exist");
    }

    private static string ColumnMap(JsonElement map)
        => string.Join(';', map.EnumerateObject().Select(table => $"{table.Name}={string.Join(',', table.Value.EnumerateArray().Select(column => column.GetString()))}"));

    internal static string CreateSqliteDb(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE accounts (id INTEGER PRIMARY KEY, email TEXT, secret TEXT)";
        create.ExecuteNonQuery();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO accounts (email, secret) VALUES ($email, $secret)";
        insert.Parameters.AddWithValue("$email", "alice@example.com");
        insert.Parameters.AddWithValue("$secret", "token");
        insert.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void CliExportSqlGeneratesManifest()
    {
        var database = CreateSqliteDb(Path.Combine(_tmp.FullName, "demo.sqlite"));
        var outputDir = Path.Combine(_tmp.FullName, "exports");

        var run = CliInvocation.Invoke(
            "sql-export", database, "--output-dir", outputDir, "--mask-column", "accounts.secret", "--hash-column", "accounts.email",
            "--placeholder", "[MASK]", "--hash-salt", "pepper");

        run.ExitCode.Should().Be(0, run.Err);
        var manifestPath = Path.Combine(outputDir, "sql-manifest.json");
        File.Exists(manifestPath).Should().BeTrue();
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var exports = manifest.RootElement.GetProperty("exports");
        exports.GetArrayLength().Should().BeGreaterThan(0, "expected manifest export entries");
        var exportEntry = exports[0];
        exportEntry.GetProperty("dialect").GetString().Should().Be("sqlite");
        ColumnMap(exportEntry.GetProperty("masked_columns")).Should().Be("accounts=secret");
        ColumnMap(exportEntry.GetProperty("hashed_columns")).Should().Be("accounts=email");
        File.Exists(Path.Combine(outputDir, "demo-sql-snapshot.json")).Should().BeTrue();
    }

    [Fact]
    public void MainConsumesSysArgv()
    {
        var sample = Write("config.json", """{"enabled": true}""");

        var run = CliInvocation.Invoke("scan", sample, "--json");

        run.ExitCode.Should().Be(0, run.Err);
        var payload = run.JsonLines();
        payload.Should().NotBeEmpty();
        payload[0].GetProperty("detected").GetBoolean().Should().BeTrue();
        payload[0].GetProperty("format").GetString().Should().Be("json");
    }

    /// <summary><c>driftbuster-export-sql ARGS</c> is <c>driftbuster sql-export ARGS</c>: the arguments reach the export parser unchanged.</summary>
    [Fact]
    public void ExportSqlConsoleMainForwardsArguments()
    {
        var parse = Program.BuildRootCommand().Parse(["sql-export", "demo.sqlite", "--limit", "10"]);

        parse.Errors.Should().BeEmpty();
        parse.CommandResult.Command.Name.Should().Be("sql-export");
        parse.GetValue<string[]>("database").Should().Equal("demo.sqlite");
        parse.GetValue<System.Numerics.BigInteger?>("--limit").Should().Be(new System.Numerics.BigInteger(10));
    }

    [Fact]
    public void CliDiffGeneratesUnifiedPatch()
    {
        var baseline = Write("baseline.txt", "alpha\nsecret\n");
        var candidate = Write("candidate.txt", "alpha\nsecret updated\n");

        var run = CliInvocation.Invoke("diff", baseline, candidate, "--mask-token", "secret");

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Should().Contain("baseline.txt").And.Contain("candidate.txt").And.Contain("[REDACTED]").And.Contain("Summary:");
    }

    [Fact]
    public void CliDiffWritesPatchFiles()
    {
        var baseline = Write("config.xml", "<root>\n  <value>1</value>\n</root>\n");
        var candidate = Write("config-release.xml", "<root>\n  <value>2</value>\n</root>\n");
        var outputDir = Path.Combine(_tmp.FullName, "patches");

        var run = CliInvocation.Invoke("diff", baseline, candidate, "--output-dir", outputDir, "--context-lines", "1");

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Should().Contain("Wrote diff");
        var patchFile = Path.Combine(outputDir, "config--config-release.patch");
        File.Exists(patchFile).Should().BeTrue();
        var patchContents = File.ReadAllText(patchFile, Utf8);
        patchContents.Should().StartWith("--- config.xml");
        patchContents.Should().Contain("+<root><value>2</value></root>");
    }

    [Fact]
    public void CliDiffSupportsMultipleComparisons()
    {
        var baseline = Write("baseline.txt", "alpha\n");
        var candidateA = Write("candidate-a.txt", "alpha\nbeta\n");
        var candidateB = Write("candidate-b.txt", "alpha\ngamma\n");
        var outputDir = Path.Combine(_tmp.FullName, "patches");

        var run = CliInvocation.Invoke("diff", baseline, candidateA, candidateB, "--output-dir", outputDir);

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Split("Wrote diff").Should().HaveCount(3);
        File.Exists(Path.Combine(outputDir, "baseline--candidate-a.patch")).Should().BeTrue();
        File.Exists(Path.Combine(outputDir, "baseline--candidate-b.patch")).Should().BeTrue();
    }

    /// <summary>Python runs <c>python -m driftbuster.cli diff</c> in a subprocess; the console tool runs the same argv in process.</summary>
    [Fact]
    public void CliDiffModuleEntrypointSupportsMultipleComparisons()
    {
        var baseline = Write("baseline.txt", "alpha\n");
        var candidateA = Write("candidate-a.txt", "alpha\nbeta\n");
        var candidateB = Write("candidate-b.txt", "alpha\ngamma\n");

        var run = CliInvocation.Invoke("diff", baseline, candidateA, candidateB);

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Should().Contain("=== baseline.txt").And.Contain("candidate-a.txt").And.Contain("candidate-b.txt");
        run.Out.Split("Summary:").Should().HaveCount(3);
    }
}
