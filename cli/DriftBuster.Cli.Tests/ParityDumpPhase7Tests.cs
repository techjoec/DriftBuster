using System.CommandLine;
using System.Text.Json;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// The sql-export, report, registry-scan and capture surfaces of <c>parity-dump</c>. Expected lines and values are tools/parity/py_dump.py's
/// output over the same inputs.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public sealed class ParityDumpPhase7Tests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-parity-phase7-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Write(string relative, string content)
    {
        var path = Path.Combine([_tmp.FullName, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Invoke(params string[] args)
    {
        var output = new StringWriter();
        var configuration = new InvocationConfiguration { Output = output, Error = new StringWriter() };
        Program.BuildRootCommand().Parse(args).Invoke(configuration).Should().Be(0);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle();
        return lines[0];
    }

    [Fact]
    public void SqlExport_prints_the_snapshot_and_the_written_file_as_python_does()
    {
        Write("db.sql", "CREATE TABLE t (id INTEGER PRIMARY KEY, secret TEXT, data BLOB);\nINSERT INTO t (secret, data) VALUES ('s1', x'00ff');\n");
        var testCase = Write("sql.json", """{"database": {"script": "db.sql"}, "options": {"mask_columns": ["t.secret"], "hash_columns": {"t": ["id"]}, "hash_salt": "salt"}}""");
        var scratch = Path.Combine(_tmp.FullName, "scratch");

        var line = Invoke("parity-dump", "sql-export", testCase, "--scratch", scratch);

        Directory.Exists(scratch).Should().BeFalse();
        line.Should().StartWith(
            """{"file": {"path": "snapshot.json", "sha256": "cfeb5c65eb9254236e921e2de70f1a825e5ad8c446f22fade6030403b80a2cc8", "size": 721, "text": "{\n  \"captured_at\": \"2025-04-05T06:07:08.090807+00:00\",""");
        line.Should().EndWith(
            """, "snapshot": {"captured_at": "2025-04-05T06:07:08.090807+00:00", "database": "case.sqlite", "dialect": "sqlite", "path": "case.sqlite", "tables": [{"columns": ["id", "secret", "data"], "hashed_columns": ["id"], "masked_columns": ["secret"], "name": "t", "row_count": 1, "rows": [{"data": {"type": "base64", "value": "AP8="}, "id": "sha256:272eca0a023e4e100f88f06a560bb2caa6117eccf66b2d98b81f2403cde8238b", "secret": "[REDACTED]"}], "schema": "CREATE TABLE t (id INTEGER PRIMARY KEY, secret TEXT, data BLOB)"}]}}""");
    }

    [Theory]
    [InlineData("""{"database": {"hex": "68656c6c6f20776f726c6468656c6c6f20776f726c6468656c6c6f20776f726c64"}}""", "DatabaseError", "file is not a database")]
    [InlineData("""{"database": {"kind": "directory"}}""", "OperationalError", "unable to open database file")]
    [InlineData("""{"database": {"kind": "missing"}, "path": "./sub/../case.sqlite"}""", "FileNotFoundError", "Database not found: sub/../case.sqlite")]
    [InlineData("""{"database": {"hex": ""}, "options": {"limit": 0}}""", "ValueError", "limit must be positive when provided")]
    public void SqlExport_prints_the_python_error_of_a_database_the_export_refuses(string content, string type, string message)
    {
        var testCase = Write("case.json", content);

        var document = JsonDocument.Parse(Invoke("parity-dump", "sql-export", testCase)).RootElement;

        document.GetProperty("error").GetProperty("type").GetString().Should().Be(type);
        document.GetProperty("error").GetProperty("message").GetString().Should().Be(message);
        document.TryGetProperty("file", out _).Should().BeFalse();
    }

    [Fact]
    public void SqlExport_refuses_a_database_spec_it_cannot_build()
    {
        var act = () => ParityDump.BuildSqlite(Path.Combine(_tmp.FullName, "x.sqlite"), new OrderedDictionary<string, object?>(StringComparer.Ordinal), _tmp.FullName);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Report_prints_every_stage_as_python_does()
    {
        var testCase = Write(
            "report.json",
            """{"matches": [{"plugin": "json", "format": "json", "variant": null, "confidence": 0.5, "reasons": ["r"], "metadata": {"k": "SECRET"}}], "mask_tokens": ["SECRET"], "hunt_hits": [{"kind": "mapping", "value": {"rule": {"name": "n", "token_name": "t"}, "path": "p", "line_number": 1, "excerpt": "SECRET"}}], "snapshot": {"operator": "op"}, "diffs": [{"kind": "binary", "before_hex": "00", "after_hex": "0001", "label": "b"}, {"kind": "result", "before": {"repeat": "a\n", "count": 3}, "after": "a\n"}]}""");

        var document = JsonDocument.Parse(Invoke("parity-dump", "report", testCase)).RootElement;

        document.GetProperty("json_lines").GetString().Should().Be(
            "{\"payload\": {\"confidence\": 0.5, \"format\": \"json\", \"metadata\": {\"k\": \"[REDACTED]\"}, \"plugin\": \"json\", \"reasons\": [\"r\"], \"variant\": null}, \"type\": \"detection\"}\n"
            + "{\"payload\": {\"excerpt\": \"[REDACTED]\", \"line_number\": 1, \"path\": \"p\", \"rule\": {\"name\": \"n\", \"token_name\": \"t\"}}, \"type\": \"hunt_hit\"}");
        document.GetProperty("html").GetString().Should().Contain("<div class=\"meta\">Generated at 2026-09-15T18:22:05.123456Z</div>")
            .And.Contain("binary:b\n- before size=1");
        document.GetProperty("summary").GetProperty("total_matches").GetInt32().Should().Be(1);
        document.GetProperty("manifest").GetProperty("legal").GetProperty("redacted_tokens").GetProperty("SECRET").GetInt32().Should().Be(1);
        document.GetProperty("manifest").GetProperty("run_metadata").GetProperty("operator").GetString().Should().Be("op");
    }

    [Fact]
    public void Report_prints_the_python_error_of_each_stage_that_raises()
    {
        var testCase = Write("refused.json", """{"matches": [], "redactor": {"tokens": ["a"], "placeholder": "x"}, "mask_tokens": ["b"]}""");

        var document = JsonDocument.Parse(Invoke("parity-dump", "report", testCase)).RootElement;

        foreach (var stage in new[] { "html", "json_lines", "json_lines_unsorted", "manifest" })
        {
            document.GetProperty(stage).GetProperty("error").GetProperty("type").GetString().Should().Be("ValueError");
            document.GetProperty(stage).GetProperty("error").GetProperty("message").GetString().Should().Be("Provide either an explicit redactor or mask_tokens, not both.");
        }

        document.GetProperty("summary").GetProperty("total_matches").GetInt32().Should().Be(0);
    }

    [Fact]
    public void RegistryScan_drives_the_fake_registry_and_the_cli_commands()
    {
        var testCase = Write(
            "registry.json",
            """
            {"tree": [
              {"hive": "HKLM", "path": "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall", "view": "64", "subkeys": ["a"]},
              {"hive": "HKLM", "path": "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\a", "values": [["DisplayName", "Contoso App"], ["DisplayVersion", 7, "REG_DWORD"]]},
              {"hive": "HKLM", "path": "Software\\Contoso\\App", "subkeys": ["S", "D"], "values": [["Server", "api.contoso.local"], ["Bin", {"$bytes": "68690a"}]]},
              {"hive": "HKLM", "path": "Software\\Contoso\\App\\S", "error": {"errno": 13}, "error_on": "both"},
              {"hive": "HKLM", "path": "Software\\Contoso\\App\\D", "values": [["X", "hidden"]], "denied": true},
              {"hive": "HKLM", "path": "Software\\Contoso\\Other", "error": {"type": "ValueError", "message": "bad"}},
              {"hive": "HKLM", "path": "Software\\Contoso\\Runtime", "error": {"type": "RuntimeError", "message": "closed"}}
            ],
             "find_roots": [{"token": "contoso"}, {"token": "x", "installed": [{"display_name": "X Y", "key_path": "K", "hive": "HKCU"}]}],
             "searches": [{"roots": [["HKLM", "Software\\Contoso\\App", null]], "max_depth": 0, "max_hits": 5, "time_budget_s": 5}, {"roots": "contoso", "patterns": ["("]},
                          {"roots": [["HKLM", "Software\\Contoso\\Other", null]]}, {"roots": [["HKLM", "Software\\Contoso\\Runtime", null]]}],
             "descriptors": ["HKLM\\x,view=32", "bad"],
             "remote_targets": [{"host": "h", "port": "12"}],
             "scan_sources": [{"registry_scan": {"token": "T", "roots": "HKCU\\X", "remote": "gw"}}],
             "remote_target_args": ["srv,port=5986"],
             "cli": [["list-apps"], ["search", "Contoso", "--keyword", "server", "--max-hits", "1", "--max-hits", "3", "--time-budget", "2.5"], ["emit-config", "T", "--alias", "a", "--remote-target", "gw"], ["suggest-roots", "Contoso"]]}
            """);

        var document = JsonDocument.Parse(Invoke("parity-dump", "registry-scan", testCase)).RootElement;

        document.GetProperty("enumerate").GetProperty("result")[0].GetProperty("version").GetString().Should().Be("7");
        document.GetProperty("find_roots")[1].GetProperty("result").GetArrayLength().Should().Be(10);
        document.GetProperty("searches")[0].GetProperty("result").EnumerateArray().Select(hit => hit.GetProperty("data_preview").GetString()).Should().Equal("api.contoso.local", "hi\n");
        document.GetProperty("searches")[1].GetProperty("error").GetProperty("type").GetString().Should().Be("PatternError");
        document.GetProperty("searches")[2].GetProperty("error").GetProperty("type").GetString().Should().Be("ValueError");
        document.GetProperty("searches")[3].GetProperty("error").GetProperty("type").GetString().Should().Be("RuntimeError");
        document.GetProperty("descriptors")[1].GetProperty("error").GetProperty("message").GetString().Should().Be("Registry root descriptor must start with HKLM\\ or HKCU\\");
        document.GetProperty("remote_targets")[0].GetProperty("result").GetProperty("port").GetInt32().Should().Be(12);
        document.GetProperty("scan_sources")[0].GetProperty("result").GetProperty("destination_name").GetString().Should().Be("registry_T");
        document.GetProperty("remote_target_args")[0].GetProperty("result").GetProperty("port").GetInt32().Should().Be(5986);
        var cli = document.GetProperty("cli");
        cli[0].GetProperty("stdout").GetString().Should().Be("Contoso App 7  [HKLM 64]  Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\a\n");
        cli[1].GetProperty("error").GetProperty("message").GetString().Should().Be("[Errno 13] Permission denied");
        cli[2].GetProperty("stdout").GetString().Should().Contain("\"alias\": \"a\"").And.Contain("\"remote\": {");
        cli[3].GetProperty("exit_code").GetInt32().Should().Be(0);
        document.GetProperty("usage")[2].GetProperty("last_error").GetString().Should().Be("PermissionError: [Errno 13] Permission denied");
    }

    [Theory]
    [InlineData("search", "V0", "--time-budget", "-0.5", "--max-depth", "-3", "--root", "HKLM\\S")]
    [InlineData("emit-config", "--alias", "a", "tok", "--keyword", "-12", "--time-budget", "1e3")]
    [InlineData("list-apps")]
    [InlineData("suggest-roots", "")]
    [InlineData("search", "-5", "--keyword", "hit")]
    [InlineData("suggest-roots", "-5")]
    [InlineData("suggest-roots", "-.5")]
    [InlineData("emit-config", "--alias", "a", "-12.5")]
    public void RegistryScan_cli_argv_inside_the_argparse_subset_is_accepted(params string[] argv)
    {
        var require = () => ParityDump.RequireRegistryCliSubset(argv);
        require.Should().NotThrow();
    }

    // Forms argparse reads differently from the dump's reader: a value that would be read as an option (-inf, -nan, a bare -, another
    // option), an abbreviated or unknown option, --option=value, -h, a value int()/float() refuses, a missing value, a missing or extra token.
    [Theory]
    [InlineData("search", "V0", "--time-budget", "-inf")]
    [InlineData("search", "V0", "--time-budget", "-nan")]
    [InlineData("search", "V0", "--keyword", "-")]
    [InlineData("search", "V0", "--keyword", "--pattern", "x")]
    [InlineData("search", "V0", "--max-d", "3")]
    [InlineData("search", "V0", "--alias", "a")]
    [InlineData("search", "V0", "--max-depth=3")]
    [InlineData("search", "V0", "-h")]
    [InlineData("search", "V0", "--max-depth", "x")]
    [InlineData("search", "V0", "--max-depth", "1.5")]
    [InlineData("search", "V0", "--time-budget", "one")]
    [InlineData("search", "V0", "--root")]
    [InlineData("search")]
    [InlineData("search", "V0", "V1")]
    [InlineData("search", "V0", "-5")]
    [InlineData("search", "-inf")]
    [InlineData("suggest-roots", "-5.")]
    [InlineData("list-apps", "x")]
    [InlineData("list-apps", "-5")]
    [InlineData("scan", "V0")]
    public void RegistryScan_cli_argv_outside_the_argparse_subset_is_refused(params string[] argv)
    {
        var require = () => ParityDump.RequireRegistryCliSubset(argv);
        require.Should().Throw<InvalidDataException>().Which.Message.Should().StartWith("registry-scan cli argv outside the documented subset");
    }

    [Fact]
    public void Capture_runs_the_steps_in_a_copy_of_the_workdir_and_lists_every_file_they_write()
    {
        Write("cap/workdir/tree/app.json", "{\"Server\": \"hunter2.corp.local\", \"Version\": \"1.2.3\"}\n");
        Write("cap/workdir/acc.sql", "CREATE TABLE accounts (id INTEGER PRIMARY KEY, secret TEXT);\nINSERT INTO accounts (secret) VALUES ('s1');\n");
        Write("cap/workdir/kept.txt", "unchanged\n");
        var testCase = Write(
            "cap/case.json",
            """
            {"databases": {"db/capture.sqlite": "acc.sql"},
             "steps": [
              {"command": "run", "args": {"root": "tree", "output_dir": "out", "capture_id": "base", "operator": "alice", "environment": "lab", "reason": "r", "mask_tokens": ["hunter2"], "skip_hunt": true, "sample_size": 4096, "profile_tags": [], "allow_unmasked": false}, "monotonic": [1, 2, 3], "env": {"USER": "u"}, "host": "h.example", "now": ["2025-01-01T00:00:00+00:00"]},
              {"command": "export-sql", "args": {"database": ["db/capture.sqlite"], "output_dir": "sql", "mask_column": ["accounts.secret"], "hash_salt": null, "limit": 5}},
              {"command": "compare", "args": {"baseline": "out/base-snapshot.json", "current": "out/none.json"}},
              {"command": "run", "args": {"root": "tree", "sample_size": 0, "operator": "a", "environment": "e", "reason": "r", "allow_unmasked": true}}
             ]}
            """);
        var scratch = Path.Combine(_tmp.FullName, "scratch");

        var document = JsonDocument.Parse(Invoke("parity-dump", "capture", testCase, "--scratch", scratch)).RootElement;

        Directory.Exists(scratch).Should().BeFalse();
        var steps = document.GetProperty("steps");
        steps[0].GetProperty("exit_code").GetInt32().Should().Be(0);
        steps[0].GetProperty("stdout").GetString().Should().Be("Snapshot written to out/base-snapshot.json\nManifest written to out/base-manifest.json\n");
        steps[0].GetProperty("stderr").GetString().Should().Be("warning: redaction filter configured but no tokens were replaced\n");
        steps[1].GetProperty("stdout").GetString().Should().Be("Exported SQL snapshot to <workdir>/sql/capture-sql-snapshot.json\n");
        steps[2].GetProperty("stderr").GetString().Should().Be("error: current snapshot not found: out/none.json\n");
        steps[3].GetProperty("error").GetProperty("type").GetString().Should().Be("ValueError");
        document.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("path").GetString())
            .Should().Equal("out/base-manifest.json", "out/base-snapshot.json", "sql/capture-sql-snapshot.json", "sql/sql-manifest.json");
        document.GetProperty("files")[1].GetProperty("text").GetString().Should().Contain("\"host\": \"h.example\"").And.Contain("\"root\": \"<workdir>/tree\"");
    }

    [Fact]
    public void PythonErrorName_names_sqlite_and_pattern_errors()
    {
        ParityDump.PythonErrorName(new Sqlite3Exception("IntegrityError", "datatype mismatch")).Should().Be("IntegrityError");
        ParityDump.PythonErrorName(new PythonReException("missing ), unterminated subpattern", 0)).Should().Be("PatternError");
        ParityDump.PythonErrorName(new PythonNotImplementedException("Non-relative patterns are unsupported")).Should().Be("NotImplementedError");
    }

    // {name for name in tables or () if name}: a falsy value is no filter, falsy entries are skipped, a truthy non-str entry can never
    // name a table, and a list entry raises Python's unhashable TypeError when the export iterates the names.
    [Fact]
    public void TableNames_follow_pythons_set_comprehension()
    {
        ParityDump.TableNames(null).Should().BeNull();
        ParityDump.TableNames(new List<object?>()).Should().BeNull();
        ParityDump.TableNames(0L).Should().BeNull();
        ParityDump.TableNames(new List<object?> { 1L, "t2", 0L, string.Empty, false, 2.5 })!.Should().Equal("\0non-str", "t2", "\0non-str");
        ParityDump.TableNames("ab")!.Should().Equal("a", "b");

        var unhashable = () => ParityDump.TableNames(new List<object?> { new List<object?> { 1L } })!.ToList();
        unhashable.Should().Throw<PythonTypeException>().WithMessage("unhashable type: 'list'");
        var scalar = () => ParityDump.TableNames(5L)!.ToList();
        scalar.Should().Throw<PythonTypeException>().WithMessage("'int' object is not iterable");
    }
}
