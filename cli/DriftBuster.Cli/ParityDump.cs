using System.CommandLine;
using System.Security.Cryptography;
using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli;

/// <summary>
/// Hidden <c>parity-dump</c> command: the C# side of tools/parity, printing the same canonical JSON lines as
/// tools/parity/py_dump.py so the two engines can be diffed byte for byte. Both sides enumerate with their own
/// <c>Detector.ScanPath</c> (tolerant of I/O errors), scan with one detector so the sampling budget spans files, and
/// emit <c>{"path", "error": "DetectorIOError"}</c> for entries the walk could not read ("." for the root itself).
/// Temporary; deleted with the Python tree.
/// </summary>
public static partial class ParityDump
{
    private const string RootErrorPath = ".";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Records I/O errors instead of raising so the walk continues past unreadable entries.</summary>
    private sealed class TolerantDetector : Detector
    {
        public TolerantDetector()
            : base(plugins: [], sortPlugins: false, maxTotalSampleBytes: long.MaxValue, onWarning: _ => { })
        {
        }

        public List<string> Errors { get; } = [];

        protected internal override void HandleError(string path, DetectorIOException error) => Errors.Add(path);
    }

    public static Command Build()
    {
        var command = new Command("parity-dump", "Canonical JSON dumps for the Python parity harness.") { Hidden = true };
        command.Subcommands.Add(BuildDetect());
        command.Subcommands.Add(BuildDecode());
        command.Subcommands.Add(BuildDiff());
        command.Subcommands.Add(BuildCanon());
        command.Subcommands.Add(BuildHunt());
        command.Subcommands.Add(BuildSecrets());
        command.Subcommands.Add(BuildSecretsContext());
        command.Subcommands.Add(BuildMultiServer());
        return command;
    }

    private static Command BuildDetect()
    {
        var pathArgument = new Argument<string>("path") { Description = "File or directory to scan." };
        var pluginsOption = new Option<string?>("--plugins") { Description = "Comma-separated plugin names to keep." };
        var sampleSizeOption = new Option<int?>("--sample-size") { Description = "Per-file sample size in bytes." };
        var budgetOption = new Option<long?>("--max-total-sample-bytes") { Description = "Aggregate sampling budget in bytes." };
        var detect = new Command("detect", "Detection results, one JSON object per file.");
        detect.Arguments.Add(pathArgument);
        detect.Options.Add(pluginsOption);
        detect.Options.Add(sampleSizeOption);
        detect.Options.Add(budgetOption);
        detect.SetAction(parseResult =>
        {
            var writer = parseResult.InvocationConfiguration.Output;
            var lines = Detect(
                parseResult.GetValue(pathArgument)!,
                parseResult.GetValue(pluginsOption),
                parseResult.GetValue(sampleSizeOption),
                parseResult.GetValue(budgetOption));
            foreach (var line in lines)
            {
                writer.Write(line);
                writer.Write('\n');
            }

            return 0;
        });
        return detect;
    }

    private static Command BuildDecode()
    {
        var pathArgument = new Argument<string>("path") { Description = "File or directory to decode." };
        var decode = new Command("decode", "looks_text, codec and text hash, one JSON object per file.");
        decode.Arguments.Add(pathArgument);
        decode.SetAction(parseResult =>
        {
            var writer = parseResult.InvocationConfiguration.Output;
            foreach (var line in Decode(parseResult.GetValue(pathArgument)!))
            {
                writer.Write(line);
                writer.Write('\n');
            }

            return 0;
        });
        return decode;
    }

    private static string Relative(string root, string path)
        => string.Equals(root, path, StringComparison.Ordinal) ? PathText.Name(root) : PathText.RelativePosix(root, path);

    /// <summary>
    /// (relative posix path, full path, errored) in <see cref="Detector.ScanPath"/> order; a root failure yields one
    /// "." entry.
    /// </summary>
    internal static IReadOnlyList<(string Relative, string Full, bool Errored)> Walk(string root)
    {
        // py_dump.py compares against Path(root), as Detector.ScanPath spells its root.
        root = PythonPurePath.Str(root);
        var enumerator = new TolerantDetector();
        var results = enumerator.ScanPath(root);
        var errored = new HashSet<string>(enumerator.Errors, StringComparer.Ordinal);
        if (errored.Contains(root) && results.Count == 0)
        {
            return [(RootErrorPath, root, true)];
        }

        // An entry the walk reported without scanning (its stat raised) is listed too, in walk order.
        var scanned = new HashSet<string>(results.Select(entry => entry.Path), StringComparer.Ordinal);
        return results.Select(entry => entry.Path)
            .Concat(enumerator.Errors.Where(path => !scanned.Contains(path) && !string.Equals(path, root, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
            .Order(Comparer<string>.Create((left, right) => PathText.ComparePosixPaths(PathText.ToPosix(left), PathText.ToPosix(right))))
            .Select(path => (Relative(root, path), path, errored.Contains(path)))
            .ToList();
    }

    internal static IReadOnlyList<IFormatPlugin>? SelectPlugins(string? names)
    {
        if (names is null)
        {
            return null;
        }

        var wanted = new HashSet<string>(names.Split(',').Select(name => name.Trim()).Where(name => name.Length > 0), StringComparer.Ordinal);
        return DefaultPlugins.GetPlugins().Where(plugin => wanted.Contains(plugin.Name)).ToList();
    }

    internal static IEnumerable<string> Detect(string root, string? plugins, int? sampleSize, long? maxTotalSampleBytes = null)
    {
        var detector = new Detector(SelectPlugins(plugins), sampleSize, maxTotalSampleBytes, onWarning: _ => { });
        foreach (var (relative, full, errored) in Walk(root))
        {
            if (errored)
            {
                yield return CanonicalJson.Serialize(ErrorRecord(relative, "DetectorIOError"));
                continue;
            }

            yield return CanonicalJson.Serialize(DetectRecord(detector, relative, full));
            if (detector.SampleBudgetExhausted)
            {
                yield break;
            }
        }
    }

    private static OrderedDictionary<string, object?> ErrorRecord(string relative, string error)
        => new(StringComparer.Ordinal) { ["path"] = relative, ["error"] = error };

    private static OrderedDictionary<string, object?> DetectRecord(Detector detector, string relative, string full)
    {
        var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["path"] = relative };
        DetectionMatch? match;
        try
        {
            match = detector.ScanFile(full);
        }
        catch (MetadataValidationError exc)
        {
            return ErrorRecord(relative, $"MetadataValidationError: {exc.Message}");
        }
        catch (DetectorIOException)
        {
            return ErrorRecord(relative, "DetectorIOError");
        }

        if (match is null)
        {
            record["plugin"] = null;
            record["format"] = null;
            record["variant"] = null;
            record["confidence"] = null;
            record["reasons"] = new List<object?>();
            record["metadata"] = null;
            return record;
        }

        record["plugin"] = match.PluginName;
        record["format"] = match.FormatName;
        record["variant"] = match.Variant;
        record["confidence"] = Math.Round(match.Confidence, 6);
        record["reasons"] = match.Reasons.ToList();
        record["metadata"] = match.Metadata is null ? null : DetectionMetadata.JsonSafe(match.Metadata);
        return record;
    }

    internal static IEnumerable<string> Decode(string root)
    {
        foreach (var (relative, full, errored) in Walk(root))
        {
            if (errored)
            {
                yield return CanonicalJson.Serialize(ErrorRecord(relative, "DetectorIOError"));
                continue;
            }

            var sample = File.ReadAllBytes(PythonPath.KernelPath(full));
            var (text, codec) = FormatRegistry.DecodeText(sample);
            var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = relative,
                ["looks_text"] = FormatRegistry.LooksText(sample),
                ["codec"] = codec,
                ["text_sha256"] = Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(text))),
            };
            yield return CanonicalJson.Serialize(record);
        }
    }
}
