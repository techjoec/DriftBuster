using System.CommandLine;
using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli;

/// <summary>The <c>diff</c> and <c>canon</c> surfaces of <c>parity-dump</c> (py_dump.py <c>cmd_diff</c>, <c>cmd_canon</c>).</summary>
public static partial class ParityDump
{
    private const string GeneratedAtToken = "<generated_at>";

    // An engine exception: no exception type, which differs between the runtimes.
    private const string EngineError = "EngineError";

    // bytes.decode("utf-8", "replace"): no BOM stripping, each maximal invalid subsequence becomes U+FFFD.
    private static readonly UTF8Encoding ReplacingUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly string[] ContentTypeChoices = ["text", "json", "xml"];

    internal sealed record DiffOptions(string? ContentType, int Context, IReadOnlyList<string> Masks, string Placeholder, string? LabelFrom, string? LabelTo);

    private static string ReadReplace(string path) => ReplacingUtf8.GetString(File.ReadAllBytes(path));

    private static Command BuildDiff()
    {
        var beforeArgument = new Argument<string?>("before") { Arity = ArgumentArity.ZeroOrOne, Description = "Baseline file." };
        var afterArgument = new Argument<string?>("after") { Arity = ArgumentArity.ZeroOrOne, Description = "Comparison file." };
        var pairsOption = new Option<string?>("--pairs") { Description = "Tab-separated before/after lines." };
        var contentTypeOption = new Option<string?>("--content-type") { Description = "text, json or xml; detection when omitted." };
        contentTypeOption.AcceptOnlyFromAmong(ContentTypeChoices);
        var contextOption = new Option<int>("--context") { DefaultValueFactory = _ => 3, Description = "Context lines." };
        var maskOption = new Option<string[]>("--mask") { Description = "Token to mask (repeatable).", AllowMultipleArgumentsPerToken = false };
        var placeholderOption = new Option<string>("--placeholder") { DefaultValueFactory = _ => "[REDACTED]", Description = "Mask placeholder." };
        var labelFromOption = new Option<string?>("--label-from") { Description = "From label; the file name when omitted." };
        var labelToOption = new Option<string?>("--label-to") { Description = "To label; the file name when omitted." };
        var diff = new Command("diff", "build_unified_diff result and summary payload per pair.");
        diff.Arguments.Add(beforeArgument);
        diff.Arguments.Add(afterArgument);
        foreach (var option in new Option[] { pairsOption, contentTypeOption, contextOption, maskOption, placeholderOption, labelFromOption, labelToOption })
        {
            diff.Options.Add(option);
        }

        diff.SetAction(parseResult =>
        {
            var options = new DiffOptions(
                parseResult.GetValue(contentTypeOption),
                parseResult.GetValue(contextOption),
                parseResult.GetValue(maskOption) ?? [],
                parseResult.GetValue(placeholderOption)!,
                parseResult.GetValue(labelFromOption),
                parseResult.GetValue(labelToOption));
            var pairs = parseResult.GetValue(pairsOption);
            var before = parseResult.GetValue(beforeArgument);
            var after = parseResult.GetValue(afterArgument);
            if (pairs is null && (before is null || after is null))
            {
                parseResult.InvocationConfiguration.Error.WriteLine("diff needs <before> <after> or --pairs");
                return 2;
            }

            var list = pairs is not null ? ReadPairs(pairs) : [(before!, after!)];
            WriteLines(parseResult, Diff(list, options));
            return 0;
        });
        return diff;
    }

    private static Command BuildCanon()
    {
        var pathArgument = new Argument<string>("path") { Description = "File or directory to canonicalise." };
        var contentTypeOption = new Option<string>("--content-type") { Required = true, Description = "text, json or xml." };
        contentTypeOption.AcceptOnlyFromAmong(ContentTypeChoices);
        var canon = new Command("canon", "Canonical text per file.");
        canon.Arguments.Add(pathArgument);
        canon.Options.Add(contentTypeOption);
        canon.SetAction(parseResult =>
        {
            WriteLines(parseResult, Canon(parseResult.GetValue(pathArgument)!, parseResult.GetValue(contentTypeOption)!));
            return 0;
        });
        return canon;
    }

    private static void WriteLines(ParseResult parseResult, IEnumerable<string> lines)
    {
        var writer = parseResult.InvocationConfiguration.Output;
        foreach (var line in lines)
        {
            writer.Write(line);
            writer.Write('\n');
        }
    }

    private static List<(string Before, string After)> ReadPairs(string path)
        => TextLines.SplitLines(File.ReadAllText(path, Encoding.UTF8))
            .Where(line => line.Length > 0)
            .Select(line => line.Split('\t'))
            .Select(parts => (parts[0], parts[1]))
            .ToList();

    internal static IEnumerable<string> Diff(IEnumerable<(string Before, string After)> pairs, DiffOptions options)
    {
        foreach (var (before, after) in pairs)
        {
            var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["pair"] = before + "\t" + after };
            try
            {
                foreach (var (key, value) in DiffRecord(before, after, options))
                {
                    record[key] = value;
                }
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException)
            {
                record["error"] = EngineError;
            }

            yield return CanonicalJson.Serialize(record);
        }
    }

    // Path(before) and Path(after): the pair is spelled as pathlib spells it (a trailing separator dropped) before any read.
    private static OrderedDictionary<string, object?> DiffRecord(string rawBefore, string rawAfter, DiffOptions options)
    {
        var before = PythonPurePath.Str(rawBefore);
        var after = PythonPurePath.Str(rawAfter);
        var contentType = options.ContentType ?? ContentTypeResolver.ResolvePair(before, after);
        var artifact = DiffBuilder.BuildUnifiedDiff(
            ReadReplace(before),
            ReadReplace(after),
            contentType,
            options.LabelFrom ?? PathText.Name(before),
            options.LabelTo ?? PathText.Name(after),
            maskTokens: options.Masks.Count > 0 ? options.Masks : null,
            placeholder: options.Placeholder,
            contextLines: options.Context);
        var summary = DiffBuilder.DiffSummaryToPayload(DiffBuilder.SummariseDiffResult(artifact, [artifact.FromLabel, artifact.ToLabel]));
        summary["generated_at"] = GeneratedAtToken;
        var result = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonical_before"] = artifact.CanonicalBefore,
            ["canonical_after"] = artifact.CanonicalAfter,
            ["diff"] = artifact.Diff,
            ["stats"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["added_lines"] = artifact.Stats.AddedLines,
                ["removed_lines"] = artifact.Stats.RemovedLines,
                ["changed_lines"] = artifact.Stats.ChangedLines,
            },
            ["content_type"] = artifact.ContentType,
            ["from_label"] = artifact.FromLabel,
            ["to_label"] = artifact.ToLabel,
            ["label"] = artifact.Label,
            ["mask_tokens"] = artifact.MaskTokens?.Cast<object?>().ToList(),
            ["placeholder"] = artifact.Placeholder,
            ["context_lines"] = artifact.ContextLines,
            ["redaction_counts"] = artifact.RedactionCounts is null ? null : new OrderedDictionary<string, object?>(
                artifact.RedactionCounts.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value)),
                StringComparer.Ordinal),
            ["safety_limits"] = artifact.SafetyLimits?.ToPayload(),
        };
        var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["result"] = result, ["summary"] = summary };
        record["key_order"] = CanonicalJson.KeyOrder(record);
        return record;
    }

    internal static IEnumerable<string> Canon(string root, string contentType)
    {
        foreach (var (relative, full, errored) in Walk(root))
        {
            var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["path"] = relative, ["content_type"] = contentType };
            if (errored)
            {
                record["error"] = "DetectorIOError";
            }
            else
            {
                try
                {
                    record["canonical"] = Canonicaliser.Canonicalise(ReadReplace(full), contentType);
                }
                catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    record["error"] = EngineError;
                }
            }

            yield return CanonicalJson.Serialize(record);
        }
    }
}
