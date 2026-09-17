using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Diff;

/// <summary>
/// <c>_enforce_diff_safety_limits</c> and the <c>safety_limits</c> mapping it returns when anything was clamped:
/// <c>thresholds</c> always, then <c>canonical</c> (<c>before</c> and/or <c>after</c>) and <c>diff</c> when those were
/// truncated. Sizes are UTF-8 byte counts; digests are taken over the full payload before clamping.
/// </summary>
/// <remarks>
/// An unpaired surrogate is encoded as U+FFFD instead of aborting the diff, which the strict UTF-8 codec would do.
/// </remarks>
public sealed class DiffSafetyLimits
{
    /// <summary><c>_SAFE_DIFF_MAX_CANONICAL_BYTES</c>: 256 KiB per canonical payload.</summary>
    public const int DefaultMaxCanonicalBytes = 256 * 1024;

    /// <summary><c>_SAFE_DIFF_MAX_DIFF_BYTES</c>: 128 KiB of unified diff output.</summary>
    public const int DefaultMaxDiffBytes = 128 * 1024;

    /// <summary><c>_SAFE_DIFF_MAX_DIFF_LINES</c>: 600 rendered diff lines.</summary>
    public const int DefaultMaxDiffLines = 600;

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    // bytes.decode("utf-8", "ignore"): invalid and truncated sequences vanish.
    private static readonly Encoding Utf8Ignore = Encoding.GetEncoding(
        "utf-8",
        EncoderFallback.ReplacementFallback,
        new DecoderReplacementFallback(string.Empty));

    // Test seams for the limits.
    internal static int MaxCanonicalBytes { get; set; } = DefaultMaxCanonicalBytes;

    internal static int MaxDiffBytes { get; set; } = DefaultMaxDiffBytes;

    internal static int MaxDiffLines { get; set; } = DefaultMaxDiffLines;

    [JsonPropertyName("thresholds")]
    public DiffSafetyThresholds Thresholds { get; init; } = new();

    [JsonPropertyName("canonical")]
    public CanonicalLimits? Canonical { get; set; }

    [JsonPropertyName("diff")]
    public DiffOutputTruncation? Diff { get; set; }

    public sealed class DiffSafetyThresholds
    {
        [JsonPropertyName("canonical_bytes")]
        public int CanonicalBytes { get; init; }

        [JsonPropertyName("diff_bytes")]
        public int DiffBytes { get; init; }

        [JsonPropertyName("diff_lines")]
        public int DiffLines { get; init; }
    }

    public sealed class CanonicalLimits
    {
        [JsonPropertyName("before")]
        public CanonicalTruncation? Before { get; set; }

        [JsonPropertyName("after")]
        public CanonicalTruncation? After { get; set; }
    }

    public sealed class CanonicalTruncation
    {
        [JsonPropertyName("size_bytes")]
        public int SizeBytes { get; init; }

        [JsonPropertyName("truncated_bytes")]
        public int TruncatedBytes { get; init; }

        [JsonPropertyName("digest")]
        public string Digest { get; init; } = string.Empty;
    }

    public sealed class DiffOutputTruncation
    {
        [JsonPropertyName("total_lines")]
        public int TotalLines { get; init; }

        [JsonPropertyName("total_bytes")]
        public int TotalBytes { get; init; }

        [JsonPropertyName("truncated_lines")]
        public int TruncatedLines { get; init; }

        [JsonPropertyName("truncated_bytes")]
        public int TruncatedBytes { get; init; }

        [JsonPropertyName("digest")]
        public string Digest { get; init; } = string.Empty;
    }

    /// <summary>The mapping in its fixed key order, for JSON payloads.</summary>
    public OrderedDictionary<string, object?> ToPayload()
    {
        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["thresholds"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["canonical_bytes"] = Thresholds.CanonicalBytes,
                ["diff_bytes"] = Thresholds.DiffBytes,
                ["diff_lines"] = Thresholds.DiffLines,
            },
        };
        if (Canonical is not null)
        {
            var canonical = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (label, info) in new[] { ("before", Canonical.Before), ("after", Canonical.After) })
            {
                if (info is not null)
                {
                    canonical[label] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["size_bytes"] = info.SizeBytes,
                        ["truncated_bytes"] = info.TruncatedBytes,
                        ["digest"] = info.Digest,
                    };
                }
            }

            payload["canonical"] = canonical;
        }

        if (Diff is not null)
        {
            payload["diff"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["total_lines"] = Diff.TotalLines,
                ["total_bytes"] = Diff.TotalBytes,
                ["truncated_lines"] = Diff.TruncatedLines,
                ["truncated_bytes"] = Diff.TruncatedBytes,
                ["digest"] = Diff.Digest,
            };
        }

        return payload;
    }

    /// <summary>
    /// <c>_enforce_diff_safety_limits(canonical_before, canonical_after, diff_text)</c>: the inputs unchanged and null
    /// limits when nothing exceeds a threshold, otherwise the clamped values and the limits.
    /// </summary>
    public static (string Before, string After, string Diff, DiffSafetyLimits? Limits) Enforce(string canonicalBefore, string canonicalAfter, string diffText)
    {
        ArgumentNullException.ThrowIfNull(canonicalBefore);
        ArgumentNullException.ThrowIfNull(canonicalAfter);
        ArgumentNullException.ThrowIfNull(diffText);
        var limits = new DiffSafetyLimits
        {
            Thresholds = new DiffSafetyThresholds
            {
                CanonicalBytes = MaxCanonicalBytes,
                DiffBytes = MaxDiffBytes,
                DiffLines = MaxDiffLines,
            },
        };

        var (clampedBefore, beforeInfo) = TruncateCanonicalPayload(canonicalBefore, "before");
        var (clampedAfter, afterInfo) = TruncateCanonicalPayload(canonicalAfter, "after");
        if (beforeInfo is not null || afterInfo is not null)
        {
            limits.Canonical = new CanonicalLimits { Before = beforeInfo, After = afterInfo };
        }

        var (clampedDiff, diffInfo) = TruncateDiffOutput(diffText);
        limits.Diff = diffInfo;
        if (beforeInfo is null && afterInfo is null && diffInfo is null)
        {
            return (canonicalBefore, canonicalAfter, diffText, null);
        }

        return (clampedBefore, clampedAfter, clampedDiff, limits);
    }

    /// <summary><c>_digest(value)</c>: <c>sha256:</c> and the lower-case hex SHA-256 of the UTF-8 bytes.</summary>
    public static string Digest(string value) => DigestBytes(Utf8.GetBytes(value));

    /// <summary><c>_digest_bytes(payload)</c>.</summary>
    public static string DigestBytes(byte[] payload)
        => "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));

    private static (string Text, CanonicalTruncation? Info) TruncateCanonicalPayload(string payload, string label)
    {
        var encoded = Utf8.GetBytes(payload);
        var sizeBytes = encoded.Length;
        if (sizeBytes <= MaxCanonicalBytes)
        {
            return (payload, null);
        }

        var digest = DigestBytes(encoded);
        var truncatedBytes = sizeBytes - MaxCanonicalBytes;
        var safePayload = Utf8Ignore.GetString(encoded, 0, MaxCanonicalBytes);
        var notice = string.Create(CultureInfo.InvariantCulture, $"\u2026 [canonical {label} truncated {truncatedBytes} bytes for safety; digest={digest}]");
        var info = new CanonicalTruncation { SizeBytes = sizeBytes, TruncatedBytes = truncatedBytes, Digest = digest };
        return (AppendNotice(safePayload, notice), info);
    }

    private static (string Text, DiffOutputTruncation? Info) TruncateDiffOutput(string diffText)
    {
        if (diffText.Length == 0)
        {
            return (diffText, null);
        }

        var lines = TextLines.SplitLines(diffText);
        var totalLines = lines.Count;
        var fullBytes = Utf8.GetBytes(diffText);
        var totalBytes = fullBytes.Length;
        var digest = DigestBytes(fullBytes);
        var truncatedLines = 0;
        var truncatedBytes = 0;
        if (totalLines > MaxDiffLines)
        {
            truncatedLines = totalLines - MaxDiffLines;
            lines = lines.Take(MaxDiffLines).ToList();
        }

        var workingText = string.Join("\n", lines);
        var workingBytes = Utf8.GetBytes(workingText);
        if (workingBytes.Length > MaxDiffBytes)
        {
            truncatedBytes = workingBytes.Length - MaxDiffBytes;
            workingText = Utf8Ignore.GetString(workingBytes, 0, MaxDiffBytes);
        }

        if (truncatedLines == 0 && truncatedBytes == 0)
        {
            if (totalBytes <= MaxDiffBytes)
            {
                return (diffText, null);
            }

            truncatedBytes = totalBytes - MaxDiffBytes;
            workingText = Utf8Ignore.GetString(fullBytes, 0, MaxDiffBytes);
        }

        var segments = new List<string>();
        if (truncatedLines != 0)
        {
            segments.Add(string.Create(CultureInfo.InvariantCulture, $"{truncatedLines} lines"));
        }

        if (truncatedBytes != 0)
        {
            segments.Add(string.Create(CultureInfo.InvariantCulture, $"{truncatedBytes} bytes"));
        }

        var notice = $"\u2026 [diff truncated {string.Join(" and ", segments)} for safety; digest={digest}]";
        var info = new DiffOutputTruncation
        {
            TotalLines = totalLines,
            TotalBytes = totalBytes,
            TruncatedLines = truncatedLines,
            TruncatedBytes = truncatedBytes,
            Digest = digest,
        };
        return (AppendNotice(workingText, notice), info);
    }

    // _append_notice: strip trailing LF characters, then the notice on its own line (or alone when nothing is left).
    private static string AppendNotice(string text, string notice)
    {
        var stripped = text.TrimEnd('\n');
        return stripped.Length > 0 ? stripped + "\n" + notice : notice;
    }
}
