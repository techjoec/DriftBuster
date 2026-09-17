using System.CommandLine;
using System.Globalization;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Remote;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Cli;

/// <summary>The <c>capture</c> surface of <c>parity-dump</c> (py_dump.py <c>cmd_capture</c>).</summary>
public static partial class ParityDump
{
    private const string CaptureNow = "2025-03-12T01:02:03.456789+00:00";

    /// <summary>py_dump.py <c>_RepeatingQueue</c>: each call takes the next value; once the list is spent the last value repeats.</summary>
    private sealed class RepeatingQueue<T>(IReadOnlyList<T> values, T fallback)
    {
        private readonly IReadOnlyList<T> _values = values.Count > 0 ? values : [fallback];
        private int _index;

        public T Next() => _values[Math.Min(_index++, _values.Count - 1)];
    }

    private static Command BuildCapture()
    {
        var caseArgument = new Argument<string>("case") { Description = "capture case.json (its workdir/ beside it)." };
        var scratchOption = new Option<string?>("--scratch") { Description = "Directory created for the run and removed after it." };
        var command = new Command("capture", "capture.py run, export-sql and compare steps and every file they write.");
        command.Arguments.Add(caseArgument);
        command.Options.Add(scratchOption);
        command.SetAction(parseResult =>
        {
            WriteLines(parseResult, [Capture(parseResult.GetValue(caseArgument)!, parseResult.GetValue(scratchOption))]);
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Copies the case's <c>workdir/</c> to <c>&lt;scratch&gt;/work</c>, builds its <c>databases</c> (<c>{relative path: script}</c>), makes it
    /// the working directory and runs each step (<see cref="CaptureStep"/>); prints <c>steps</c> and <c>files</c> (every file under the working
    /// directory the steps created or changed, by code point order of its posix path, with size, SHA-256 and text), the working directory
    /// respelled <c>&lt;workdir&gt;</c>. The scratch directory is removed afterwards.
    /// </summary>
    internal static string Capture(string casePath, string? scratchPath)
    {
        var caseFull = Path.GetFullPath(casePath);
        var testCase = LoadCase(caseFull);
        var scratch = scratchPath ?? Directory.CreateTempSubdirectory("driftbuster-parity-capture-").FullName;
        Directory.CreateDirectory(scratch);
        var work = Path.Combine(scratch, "work");
        var previous = Environment.CurrentDirectory;
        try
        {
            CopyTree(Path.Combine(Path.GetDirectoryName(caseFull)!, "workdir"), work);
            if (testCase.GetValueOrDefault("databases") is IReadOnlyDictionary<string, object?> databases)
            {
                foreach (var (relative, script) in databases)
                {
                    BuildSqlite(Path.Combine(work, relative), new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["script"] = script }, work);
                }
            }

            Environment.CurrentDirectory = work;
            var prefix = Environment.CurrentDirectory;
            var before = TreeFiles(work);
            var steps = Items(testCase, "steps").Select(object? (step) => CaptureStep((IReadOnlyDictionary<string, object?>)step!)).ToList();
            var after = TreeFiles(work);
            var relatives = after.Keys.ToList();
            relatives.Sort(PathText.CompareCodePoints);
            var files = relatives
                .Where(relative => !before.TryGetValue(relative, out var old) || !old.AsSpan().SequenceEqual(after[relative]))
                .Select(object? (relative) => FileEntry(Path.Combine(work, relative), relative, prefix))
                .ToList();
            var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["steps"] = steps, ["files"] = files };
            return Keyed((OrderedDictionary<string, object?>)Respell(record, prefix)!);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(scratch, recursive: true);
        }
    }

    // py_dump.py _tree_files: root.rglob("*") (which does not descend into a symlinked directory) filtered to regular files that are not
    // symlinks, by posix relative path.
    private static Dictionary<string, byte[]> TreeFiles(string root)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null)
                {
                    continue;
                }

                if (entry is DirectoryInfo directory)
                {
                    pending.Push(directory);
                }
                else if (PythonPath.IsFile(entry.FullName))
                {
                    files[PathText.RelativePosix(root, entry.FullName)] = File.ReadAllBytes(entry.FullName);
                }
            }
        }

        return files;
    }

    /// <summary>
    /// py_dump.py <c>_capture_step</c>: one <see cref="CaptureRunner"/> command over its options (the parser's defaults with the step's
    /// <c>args</c>), with <see cref="CaptureRunner.UtcNow"/> and <see cref="SqliteSnapshots.UtcNow"/> sharing the <c>now</c> queue, the monotonic
    /// timer on <c>monotonic</c>, the host name on <c>host</c> and the environment on <c>env</c>: <c>command</c>, <c>exit_code</c> or
    /// <c>error</c>, <c>stdout</c> and <c>stderr</c>.
    /// </summary>
    private static OrderedDictionary<string, object?> CaptureStep(IReadOnlyDictionary<string, object?> step)
    {
        var command = (string)step["command"]!;
        var args = step.GetValueOrDefault("args") as IReadOnlyDictionary<string, object?> ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var now = new RepeatingQueue<string>(Items(step, "now").Select(stamp => (string)stamp!).ToList(), CaptureNow);
        var clock = new RepeatingQueue<double>(Items(step, "monotonic").Select(PythonBuiltins.Float).ToList(), 100.0);
        var environment = step.GetValueOrDefault("env") as IReadOnlyDictionary<string, object?> ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var host = (string?)step.GetValueOrDefault("host") ?? "capture-host.example";
        var originals = (CaptureRunner.UtcNow, SqliteSnapshots.UtcNow, CaptureRunner.Monotonic, CaptureRunner.HostName, CaptureRunner.GetEnvironmentVariable);
        CaptureRunner.UtcNow = () => PythonDateTime.FromIsoFormat(now.Next());
        SqliteSnapshots.UtcNow = CaptureRunner.UtcNow;
        CaptureRunner.Monotonic = clock.Next;
        CaptureRunner.HostName = () => host;
        CaptureRunner.GetEnvironmentVariable = name => environment.GetValueOrDefault(name) as string;
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var result = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["command"] = command };
        try
        {
            result["exit_code"] = command switch
            {
                "run" => CaptureRunner.RunCapture(CaptureRunArgs(args), stdout, stderr).ExitCode,
                "export-sql" => CaptureRunner.RunSqlExport(SqlExportArgs(args), stdout, stderr).ExitCode,
                "compare" => CaptureRunner.CompareSnapshots(new CaptureCompareOptions((string)args["baseline"]!, (string)args["current"]!), stdout, stderr).ExitCode,
                _ => throw new InvalidDataException($"unknown capture command {command}"),
            };
        }
        catch (Exception exc) when (exc is not OutOfMemoryException and not InvalidDataException)
        {
            result["error"] = ErrorPayload(exc);
        }
        finally
        {
            (CaptureRunner.UtcNow, SqliteSnapshots.UtcNow, CaptureRunner.Monotonic, CaptureRunner.HostName, CaptureRunner.GetEnvironmentVariable) = originals;
        }

        result["stdout"] = stdout.ToString();
        result["stderr"] = stderr.ToString();
        return result;
    }

    private static CaptureRunOptions CaptureRunArgs(IReadOnlyDictionary<string, object?> args)
    {
        var defaults = new CaptureRunOptions();
        List<string> List(string key, IReadOnlyList<string> fallback) => args.TryGetValue(key, out var value) ? Strings(value) : [.. fallback];
        string? Text(string key, string? fallback) => args.TryGetValue(key, out var value) ? (string?)value : fallback;
        return new CaptureRunOptions
        {
            Root = Text("root", defaults.Root)!,
            Profiles = Text("profiles", defaults.Profiles),
            ProfileTags = List("profile_tags", defaults.ProfileTags),
            Glob = Text("glob", defaults.Glob)!,
            HuntGlob = Text("hunt_glob", defaults.HuntGlob)!,
            HuntExclude = List("hunt_exclude", defaults.HuntExclude),
            SkipHunt = args.TryGetValue("skip_hunt", out var skip) ? PythonBuiltins.IsTruthy(skip) : defaults.SkipHunt,
            SampleSize = args.TryGetValue("sample_size", out var size) ? (long)PythonBuiltins.Int(size) : defaults.SampleSize,
            OutputDir = Text("output_dir", defaults.OutputDir)!,
            CaptureId = Text("capture_id", defaults.CaptureId),
            Operator = Text("operator", defaults.Operator),
            Environment = Text("environment", defaults.Environment),
            Reason = Text("reason", defaults.Reason),
            MaskTokens = List("mask_tokens", defaults.MaskTokens),
            Placeholder = Text("placeholder", defaults.Placeholder)!,
            AllowUnmasked = args.TryGetValue("allow_unmasked", out var unmasked) ? PythonBuiltins.IsTruthy(unmasked) : defaults.AllowUnmasked,
            RegistryScan = List("registry_scan", defaults.RegistryScan),
        };
    }

    private static SqlExportOptions SqlExportArgs(IReadOnlyDictionary<string, object?> args)
    {
        var defaults = new SqlExportOptions();
        List<string> List(string key, IReadOnlyList<string> fallback) => args.TryGetValue(key, out var value) ? Strings(value) : [.. fallback];
        string? Text(string key, string? fallback) => args.TryGetValue(key, out var value) ? (string?)value : fallback;
        return new SqlExportOptions
        {
            Database = List("database", defaults.Database),
            OutputDir = Text("output_dir", defaults.OutputDir)!,
            Table = List("table", defaults.Table),
            ExcludeTable = List("exclude_table", defaults.ExcludeTable),
            MaskColumn = List("mask_column", defaults.MaskColumn),
            HashColumn = List("hash_column", defaults.HashColumn),
            Placeholder = Text("placeholder", defaults.Placeholder),
            HashSalt = Text("hash_salt", defaults.HashSalt),
            Limit = args.GetValueOrDefault("limit") is { } limit ? PythonBuiltins.Int(limit) : defaults.Limit,
            Prefix = Text("prefix", defaults.Prefix),
        };
    }
}
