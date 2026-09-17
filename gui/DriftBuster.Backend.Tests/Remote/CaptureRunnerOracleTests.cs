using System.Globalization;
using System.Numerics;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Remote;

namespace DriftBuster.Backend.Tests.Remote;

/// <summary>
/// The capture runner against CPython 3.13's <c>scripts/capture.py</c> (<see cref="CaptureOracleData"/>): whole <c>run</c>, <c>export-sql</c>
/// and <c>compare</c> commands over generated trees with the clock, monotonic timer, host name and environment pinned, compared on exit code
/// (or escaped exception), stdout, stderr and every file written; registry scan summaries over adversarial payloads; and manifest payloads
/// over duration values that exercise <c>round(x, 3)</c>. Files are compared with LF line breaks (the oracle ran on Linux).
/// </summary>
[Collection(CaptureSeamCollection.Name)]
public sealed class CaptureRunnerOracleTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-capture-oracle-");

    public static TheoryData<string> RunCases => CaptureOracleData.Names("run");

    public static TheoryData<string> ExportSqlCases => CaptureOracleData.Names("export_sql");

    public static TheoryData<string> CompareCases => CaptureOracleData.Names("compare");

    public static TheoryData<int> RegistrySummaryCases => CaptureOracleData.Indexes("registry_summaries");

    public static TheoryData<int> ManifestCases => CaptureOracleData.Indexes("manifest");

    private string CaseDirectory => PythonPath.Resolve(_tmp.FullName);

    public void Dispose() => _tmp.Delete(recursive: true);

    [Theory]
    [MemberData(nameof(RunCases))]
    public void RunCaptureMatchesPython(string name)
    {
        var entry = CaptureOracleData.Case("run", name);
        var options = (OrderedDictionary<string, object?>)entry["options"]!;
        var directory = CaseDirectory;
        var runOptions = new CaptureRunOptions
        {
            Root = CaptureOracleData.Expand((string)options["root"]!, directory),
            Profiles = CaptureOracleData.OptionalString(options["profiles"], directory),
            ProfileTags = CaptureOracleData.Strings(options["profile_tags"], directory),
            Glob = (string)options["glob"]!,
            HuntGlob = (string)options["hunt_glob"]!,
            HuntExclude = CaptureOracleData.Strings(options["hunt_exclude"], directory),
            SkipHunt = (bool)options["skip_hunt"]!,
            SampleSize = CaptureOracleData.Long(options["sample_size"]),
            OutputDir = CaptureOracleData.Expand((string)options["output_dir"]!, directory),
            CaptureId = CaptureOracleData.OptionalString(options["capture_id"], directory),
            Operator = CaptureOracleData.OptionalString(options["operator"], directory),
            Environment = CaptureOracleData.OptionalString(options["environment"], directory),
            Reason = CaptureOracleData.OptionalString(options["reason"], directory),
            MaskTokens = CaptureOracleData.Strings(options["mask_tokens"], directory),
            Placeholder = (string)options["placeholder"]!,
            AllowUnmasked = (bool)options["allow_unmasked"]!,
            RegistryScan = CaptureOracleData.Strings(options["registry_scan"], directory),
        };

        AssertCommand(entry, directory, (stdout, stderr) => CaptureRunner.RunCapture(runOptions, stdout, stderr).ExitCode);
    }

    [Theory]
    [MemberData(nameof(ExportSqlCases))]
    public void RunSqlExportMatchesPython(string name)
    {
        var entry = CaptureOracleData.Case("export_sql", name);
        var options = (OrderedDictionary<string, object?>)entry["options"]!;
        var directory = CaseDirectory;
        var exportOptions = new SqlExportOptions
        {
            Database = CaptureOracleData.Strings(options["database"], directory),
            OutputDir = CaptureOracleData.Expand((string)options["output_dir"]!, directory),
            Table = CaptureOracleData.Strings(options["table"], directory),
            ExcludeTable = CaptureOracleData.Strings(options["exclude_table"], directory),
            MaskColumn = CaptureOracleData.Strings(options["mask_column"], directory),
            HashColumn = CaptureOracleData.Strings(options["hash_column"], directory),
            Placeholder = (string?)options["placeholder"],
            HashSalt = (string?)options["hash_salt"],
            Limit = options["limit"] is null ? null : PythonBuiltins.Int(options["limit"]),
            Prefix = (string?)options["prefix"],
        };

        AssertCommand(entry, directory, (stdout, stderr) => CaptureRunner.RunSqlExport(exportOptions, stdout, stderr).ExitCode);
    }

    [Theory]
    [MemberData(nameof(CompareCases))]
    public void CompareSnapshotsMatchesPython(string name)
    {
        var entry = CaptureOracleData.Case("compare", name);
        var options = (OrderedDictionary<string, object?>)entry["options"]!;
        var directory = CaseDirectory;
        var compareOptions = new CaptureCompareOptions(
            CaptureOracleData.Expand((string)options["baseline"]!, directory),
            CaptureOracleData.Expand((string)options["current"]!, directory));

        AssertCommand(entry, directory, (stdout, stderr) => CaptureRunner.CompareSnapshots(compareOptions, stdout, stderr).ExitCode);
    }

    [Theory]
    [MemberData(nameof(RegistrySummaryCases))]
    public void SummariseRegistryScanMatchesPython(int index)
    {
        var entry = CaptureOracleData.Cases("registry_summaries")[index];
        var directory = CaseDirectory;
        var path = Path.Combine(directory, "registry_scan.json");
        File.WriteAllText(path, (string)entry["content"]!, new System.Text.UTF8Encoding(false));
        OrderedDictionary<string, object?> summary;
        try
        {
            summary = CaptureRunner.SummariseRegistryScan(path);
        }
        catch (Exception exc) when (entry.ContainsKey("error"))
        {
            CaptureOracleData.ErrorMismatch(exc, entry["error"], directory).Should().BeNull();
            return;
        }

        entry.Should().ContainKey("result", "Python raised {0}", entry.GetValueOrDefault("error"));
        Text(summary).Should().Be(CaptureOracleData.Expand(Text(entry["result"]), directory));
    }

    [Theory]
    [MemberData(nameof(ManifestCases))]
    public void BuildManifestPayloadMatchesPython(int index)
    {
        var entry = CaptureOracleData.Cases("manifest")[index];
        var durations = ((List<object?>)entry["durations"]!).Select(value => CaptureOracleData.Float((string)value!)).ToList();
        var capture = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "capture",
            ["captured_at"] = "2025-03-12T00:00:00Z",
            ["root"] = "/root",
            ["operator"] = "tester",
            ["environment"] = "lab",
            ["reason"] = "validation",
            ["host"] = "test-host",
        };
        var summary = (bool)entry["with_summary"]!
            ? new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["total_profiles"] = 4, ["total_configs"] = 9, ["profiles"] = new List<object?>() }
            : null;
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? scans = (bool)entry["with_scans"]!
            ? [new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["file"] = "a.json", ["token"] = null }]
            : null;

        var manifest = CaptureRunner.BuildManifestPayload(
            capture, "/out/sub/capture-snapshot.json", "/out/sub/capture-manifest.json", durations[0], durations[1], durations[2], 3, 2, 1, summary, "[X]", 2, 5, scans);

        Canonicaliser.DumpsSorted(manifest, indent: true, ensureAscii: true).Should().Be((string)entry["dump"]!);
    }

    private static string Text(object? value) => Canonicaliser.Dumps(value, indent: false, ensureAscii: true, sortKeys: false);

    // Builds the tree, pins the seams, runs the command and compares its exit code or exception, stdout, stderr and output files.
    private static void AssertCommand(OrderedDictionary<string, object?> entry, string directory, Func<StringWriter, StringWriter, int> run)
    {
        CaptureOracleData.WriteTree(entry["tree"], directory);
        var expected = (OrderedDictionary<string, object?>)entry["result"]!;
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        using (new CaptureSeams(entry))
        {
            try
            {
                var exitCode = run(stdout, stderr);
                expected.Should().ContainKey("exit_code", "Python raised {0}", expected.GetValueOrDefault("error"));
                exitCode.Should().Be((int)PythonBuiltins.Int(expected["exit_code"]));
            }
            catch (Exception exc) when (expected.ContainsKey("error"))
            {
                CaptureOracleData.ErrorMismatch(exc, expected["error"], directory).Should().BeNull();
            }
        }

        stdout.ToString().Should().Be(CaptureOracleData.Expand((string)expected["stdout"]!, directory));
        stderr.ToString().Should().Be(CaptureOracleData.JsonDecodeText(CaptureOracleData.Expand((string)expected["stderr"]!, directory)));
        OutputFiles(Path.Combine(directory, "out")).Should().Equal(ExpectedFiles(expected["files"], directory));
    }

    private static List<KeyValuePair<string, string>> OutputFiles(string output)
    {
        if (!Directory.Exists(output))
        {
            return [];
        }

        var files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Select(path => new KeyValuePair<string, string>(
                Path.GetRelativePath(output, path).Replace('\\', '/'),
                File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal)))
            .ToList();
        files.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        return files;
    }

    private static List<KeyValuePair<string, string>> ExpectedFiles(object? files, string directory)
        => ((OrderedDictionary<string, object?>)files!)
            .Select(pair => new KeyValuePair<string, string>(pair.Key, CaptureOracleData.Expand((string)pair.Value!, directory)))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();
}
