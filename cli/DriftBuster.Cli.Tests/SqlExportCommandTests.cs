using System.Text;
using System.Text.Json;

namespace DriftBuster.Cli.Tests;

/// <summary><c>driftbuster sql-export</c>: the positive <c>--limit</c> check, <c>--manifest-name</c> and the snapshot names for several databases.</summary>
public sealed class SqlExportCommandTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-sql-export-cli-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void SeveralDatabasesWithPrefixAndManifestName()
    {
        var demo = CliTests.CreateSqliteDb(Path.Combine(_tmp.FullName, "demo.sqlite"));
        var other = CliTests.CreateSqliteDb(Path.Combine(_tmp.FullName, "other.sqlite"));
        var missing = Path.Combine(_tmp.FullName, "missing.sqlite");
        var output = Path.Combine(_tmp.FullName, "out");

        var run = CliInvocation.Invoke("sql-export", demo, other, missing, "--prefix", "pre", "--output-dir", output, "--limit", "1", "--manifest-name", "m.json");

        run.ExitCode.Should().Be(1);
        var nl = Environment.NewLine;
        run.Out.Should().Be(
            $"Exported SQL snapshot to {Path.Combine(output, "pre-demo-sql-snapshot.json")}{nl}"
            + $"Exported SQL snapshot to {Path.Combine(output, "pre-other-sql-snapshot.json")}{nl}"
            + $"Manifest written to {Path.Combine(output, "m.json")}{nl}");
        run.Err.Should().Be($"error: database not found: {missing}{nl}");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "m.json"), Utf8));
        manifest.RootElement.GetProperty("exports").GetArrayLength().Should().Be(2);
        manifest.RootElement.GetProperty("settings").GetProperty("limit").GetInt32().Should().Be(1);
    }

    [Fact]
    public void ANonPositiveLimitSkipsEachDatabase()
    {
        var demo = CliTests.CreateSqliteDb(Path.Combine(_tmp.FullName, "demo.sqlite"));
        var output = Path.Combine(_tmp.FullName, "out");

        var run = CliInvocation.Invoke("sql-export", demo, "--output-dir", output, "--limit", "0");

        run.ExitCode.Should().Be(1);
        run.Err.Should().Be("error: --limit must be positive when provided" + Environment.NewLine);
        Directory.Exists(output).Should().BeFalse("nothing is written when the limit is refused");
    }

    [Fact]
    public void ALimitThatIsNotAnIntegerIsAParseError()
    {
        var run = CliInvocation.Invoke("sql-export", "demo.sqlite", "--limit", "x");

        run.ExitCode.Should().Be(2);
        run.Err.Should().StartWith("driftbuster: error: ").And.Contain("--limit");
    }
}
