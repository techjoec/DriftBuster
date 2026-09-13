using System.Globalization;
using System.Text;

using DriftBuster.Backend.Diff;

using static DriftBuster.Backend.Tests.Diff.DiffOracleData;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>
/// <see cref="DiffBuilder.BuildUnifiedDiff"/> and the summary payload against CPython on <c>Data/build_cases.json</c>:
/// every result field, the safety-limit mapping (thresholds swapped through the same seam the Python generator
/// monkeypatches) and <c>diff_summary_to_payload</c> without <c>generated_at</c>, compared as JSON with key order kept.
/// </summary>
[Collection(DiffSafetyLimitsCollection.Name)]
public sealed class DiffBuilderParityTests
{
    private const string DataFile = "build_cases.json";

    public static TheoryData<string> CaseNames => Names(DataFile);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void BuildUnifiedDiffMatchesPython(string name)
    {
        var entry = Case(DataFile, name);
        var input = Map(entry["case"]);
        var limits = List(entry["limits"]);
        var saved = (DiffSafetyLimits.MaxCanonicalBytes, DiffSafetyLimits.MaxDiffBytes, DiffSafetyLimits.MaxDiffLines);
        DiffArtifact result;
        try
        {
            (DiffSafetyLimits.MaxCanonicalBytes, DiffSafetyLimits.MaxDiffBytes, DiffSafetyLimits.MaxDiffLines) = (Int(limits[0]), Int(limits[1]), Int(limits[2]));
            result = Build(input);
        }
        finally
        {
            (DiffSafetyLimits.MaxCanonicalBytes, DiffSafetyLimits.MaxDiffBytes, DiffSafetyLimits.MaxDiffLines) = saved;
        }

        var expected = Map(entry["result"]);
        result.CanonicalBefore.Should().Be((string)expected["canonical_before"]!);
        result.CanonicalAfter.Should().Be((string)expected["canonical_after"]!);
        result.Diff.Should().Be((string)expected["diff"]!);
        result.Placeholder.Should().Be((string)expected["placeholder"]!);
        var stats = Map(expected["stats"]);
        result.Stats.Should().Be(new DiffStats(Int(stats["added_lines"]), Int(stats["removed_lines"]), Int(stats["changed_lines"])));
        Json(result.MaskTokens?.Cast<object?>().ToList()).Should().Be(Json(expected["mask_tokens"]));
        Json(result.RedactionCounts is null ? null : new OrderedDictionary<string, object?>(result.RedactionCounts.Select(pair => KeyValuePair.Create(pair.Key, (object?)pair.Value)), StringComparer.Ordinal)).Should().Be(Json(expected["redaction_counts"]));
        Json(result.SafetyLimits?.ToPayload()).Should().Be(Json(expected["safety_limits"]));

        var payload = DiffBuilder.DiffSummaryToPayload(DiffBuilder.SummariseDiffResult(result, versions: ["v1", "v2"]));
        payload.Remove("generated_at");
        Json(payload).Should().Be(Json(entry["payload"]));
    }

    private static DiffArtifact Build(OrderedDictionary<string, object?> input)
    {
        string? Text(string key, string? fallback) => input.TryGetValue(key, out var value) ? (string?)value : fallback;
        var redactor = input.TryGetValue("redactor", out var tokens) ? new RedactionFilter(Strings(tokens)) : null;
        return DiffBuilder.BuildUnifiedDiff(
            (string)input["before"]!,
            (string)input["after"]!,
            contentType: Text("content_type", "text")!,
            fromLabel: Text("from_label", "before")!,
            toLabel: Text("to_label", "after")!,
            label: Text("label", null),
            redactor: redactor,
            maskTokens: input.TryGetValue("mask_tokens", out var mask) ? Strings(mask) : null,
            placeholder: Text("placeholder", RedactionFilter.DefaultPlaceholder)!,
            contextLines: input.TryGetValue("context_lines", out var context) ? Int(context) : 3);
    }

    [Fact]
    public void BuildBinaryDiffMatchesPythonLayout()
    {
        var result = DiffBuilder.BuildBinaryDiff([1, 2, 3], [1], label: string.Empty, reason: "sqlite");
        var before = DiffSafetyLimits.DigestBytes([1, 2, 3]);
        var after = DiffSafetyLimits.DigestBytes([1]);
        result.Diff.Should().Be($"binary:binary\n- before size=3 digest={before}\n+ after size=1 digest={after}\n\u0394 bytes: -2");
        result.BinaryEvidence![0].Reason.Should().Be("sqlite");
        result.Stats.Should().Be(new DiffStats(0, 0, 1));

        var same = DiffBuilder.BuildBinaryDiff([9], [9]);
        same.Diff.Should().NotContain("bytes:");
        same.Stats.ChangedLines.Should().Be(0);
        same.Label.Should().BeNull();
        same.ContextLines.Should().Be(0);
    }

    [Fact]
    public void SummaryPayloadFormatsGeneratedAtAsIsoformat()
    {
        var saved = DiffBuilder.UtcNow;
        try
        {
            DiffBuilder.UtcNow = () => new DateTimeOffset(2026, 9, 13, 10, 33, 0, TimeSpan.Zero).AddTicks(1234567);
            var result = DiffBuilder.BuildBinaryDiff([1], [2], label: "blob");
            var payload = DiffBuilder.DiffSummaryToPayload(DiffBuilder.SummariseDiffResult(result));
            payload["generated_at"].Should().Be("2026-09-13T10:33:00.123456+00:00");

            DiffBuilder.UtcNow = () => new DateTimeOffset(2026, 9, 13, 10, 33, 0, TimeSpan.FromHours(-5));
            DiffBuilder.DiffSummaryToPayload(DiffBuilder.SummariseDiffResult(result))["generated_at"].Should().Be("2026-09-13T15:33:00+00:00");
        }
        finally
        {
            DiffBuilder.UtcNow = saved;
        }
    }

    // Compact JSON of a Python-shaped value (dicts in insertion order, lists, strings, ints, bools, null).
    private static string Json(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case string text:
                builder.Append('"').Append(text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                break;
            case bool flag:
                builder.Append(flag ? "true" : "false");
                break;
            case int number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case System.Collections.IDictionary map:
                builder.Append('{');
                var keys = map.Keys.Cast<string>().ToList();
                for (var index = 0; index < keys.Count; index++)
                {
                    builder.Append(index == 0 ? string.Empty : ",").Append('"').Append(keys[index]).Append("\":");
                    Write(builder, map[keys[index]]);
                }

                builder.Append('}');
                break;
            case System.Collections.IEnumerable items:
                builder.Append('[');
                var first = true;
                foreach (var item in items)
                {
                    builder.Append(first ? string.Empty : ",");
                    first = false;
                    Write(builder, item);
                }

                builder.Append(']');
                break;
            default:
                throw new InvalidOperationException($"Unexpected value {value.GetType()}");
        }
    }
}
