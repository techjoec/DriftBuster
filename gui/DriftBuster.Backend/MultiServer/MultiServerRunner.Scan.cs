using System.Security.Cryptography;
using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.MultiServer;

/// <summary>The per-host scan.</summary>
public sealed partial class MultiServerRunner
{
    private static readonly UTF8Encoding ReplacingUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// The detector's budget is shared by the host's roots; each detected file becomes a record whose canonical payload comes from
    /// the cache when the signature (host, config, root fingerprint, file hash, content type, canonical form version) matches.
    /// </summary>
    private PlanScan ScanPlanCore(MultiServerPlan plan, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var configs = new OrderedDictionary<string, ConfigRecord>(StringComparer.Ordinal);
        var cachedEntries = 0;
        var totalEntries = 0;
        var fingerprint = RootFingerprint(roots);
        var skipping = PrepareDetector(cancellationToken);
        var rootPositions = PlanRootPositions(plan, roots);

        Detector.ResetSampleBudget();
        var budgetExhausted = false;
        for (var rootIndex = 0; rootIndex < roots.Count && !budgetExhausted; rootIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots[rootIndex];
            foreach (var (path, match) in Detector.ScanPath(root, "**/*", resetBudget: false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsRegularFile(path))
                {
                    continue;
                }

                if (match is null)
                {
                    if (Detector.SampleBudgetExhausted)
                    {
                        budgetExhausted = true;
                        break;
                    }

                    continue;
                }

                var built = BuildRecord(plan, root, rootPositions[rootIndex], fingerprint, path, match, configs, cancellationToken);
                if (built is { } entry)
                {
                    cachedEntries += entry.Cached ? 1 : 0;
                    totalEntries++;
                    configs[entry.Record.ConfigId] = entry.Record;
                }
                else
                {
                    skipping?.SkippedFiles.Add(path);
                }

                if (Detector.SampleBudgetExhausted)
                {
                    budgetExhausted = true;
                    break;
                }
            }
        }

        var budgetReached = budgetExhausted || Detector.SampleBudgetExhausted;
        Detector.ResetSampleBudget();
        var skipped = skipping?.SkippedFiles.ToArray() ?? [];
        return new PlanScan(configs, totalEntries > 0 && cachedEntries == totalEntries, budgetReached, skipped);
    }

    // The zero-based position of each scanned root in plan.Roots (the config id's "@root{index}"): the scanned roots are the plan's roots
    // that exist, in plan order, so each is matched to the next plan root spelled the same way. A root the plan does not hold (a
    // test seam passing its own roots) keeps its position among the scanned roots.
    private static int[] PlanRootPositions(MultiServerPlan plan, IReadOnlyList<string> roots)
    {
        var positions = new int[roots.Count];
        var next = 0;
        for (var index = 0; index < roots.Count; index++)
        {
            var found = -1;
            for (var candidate = next; candidate < plan.Roots.Count; candidate++)
            {
                if (string.Equals(LexicalPath.Str(plan.Roots[candidate]), roots[index], StringComparison.Ordinal))
                {
                    found = candidate;
                    break;
                }
            }

            positions[index] = found >= 0 ? found : index;
            next = found >= 0 ? found + 1 : next;
        }

        return positions;
    }

    private SkippingDetector? PrepareDetector(CancellationToken cancellationToken)
    {
        if (Detector is not SkippingDetector skipping)
        {
            return null;
        }

        skipping.CancellationToken = cancellationToken;
        skipping.SkippedFiles.Clear();
        return skipping;
    }

    private (ConfigRecord Record, bool Cached)? BuildRecord(
        MultiServerPlan plan,
        string root,
        int rootPosition,
        string fingerprint,
        string path,
        DetectionMatch match,
        OrderedDictionary<string, ConfigRecord> configs,
        CancellationToken cancellationToken)
    {
        var relative = LexicalPath.RelativeTo(Path.GetFullPath(path), Path.GetFullPath(root)) ?? PathText.Name(path);
        var catalogFormat = match.Metadata.Text("catalog_format");
        var formatId = !string.IsNullOrEmpty(catalogFormat) ? catalogFormat
            : string.IsNullOrEmpty(match.FormatName) ? "unknown" : match.FormatName;
        var contentType = ContentTypeResolver.FromCatalogFormat(catalogFormat);
        var configId = ConfigIdentity.Disambiguate(ConfigIdentity.NormaliseConfigId(match, relative), rootPosition, configs.ContainsKey);
        string rawText;
        try
        {
            rawText = ReadText(path, MaxTextBytes);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var fileHash = Sha256Hex(rawText);
        var signature = Sha256Hex($"{plan.HostId}:{configId}:{fingerprint}:{fileHash}:{contentType}:{Canonicaliser.FormVersion}");
        var cached = Cache.Load(plan.HostId, configId, signature);
        var canonical = cached ?? Canonicaliser.Canonicalise(rawText, contentType);
        if (cached is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Cache.Save(plan.HostId, configId, signature, canonical);
        }

        var record = new ConfigRecord
        {
            ConfigId = configId,
            DisplayName = relative,
            FormatId = formatId,
            ContentType = contentType,
            Canonical = canonical,
            Raw = rawText,
            FileHash = fileHash,
            Secrets = ContainsSecret(rawText, cancellationToken),
            Masked = match.Metadata.HasContent("has_masked_tokens"),
            SourcePath = path,
            PluginName = string.IsNullOrEmpty(match.PluginName) ? "unknown" : match.PluginName,
            RelativePath = relative,
        };
        return (record, cached is not null);
    }

    // Any line matching one of the secret scanner's rules (the embedded secret_rules.json), the same rules the profile
    // collector redacts with.
    private static bool ContainsSecret(string text, CancellationToken cancellationToken)
    {
        var rules = SecretRules.Packaged.Rules;
        return rules.Count > 0 && text.Split('\n').Any(line => rules.Any(rule => PatternRegex.Search(rule.Pattern, line, cancellationToken) is not null));
    }

    // A lookup error means the file cannot be read, not that the host fails.
    private static bool IsRegularFile(string path)
    {
        try
        {
            return FilePaths.IsFile(path);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>SHA-1 of the sorted full roots (a root that is a link as its target) joined by "|".</summary>
    internal static string RootFingerprint(IReadOnlyList<string> roots)
    {
        var resolved = roots.Select(FilePaths.ResolveLinks).ToList();
        resolved.Sort(PathText.CompareCodePoints);
        return MultiServerPlan.Sha1Hex(string.Join('|', resolved));
    }

    /// <summary>
    /// The whole file as UTF-8 with replacement and universal newlines. A file over <paramref name="maxBytes"/> throws
    /// <see cref="IOException"/> unread, since its text might not fit a string.
    /// </summary>
    internal static string ReadText(string path, long maxBytes = DefaultMaxTextBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maxBytes)
        {
            throw new IOException($"File is larger than the {maxBytes} bytes the scan reads whole: '{path}'");
        }

        using var buffer = new MemoryStream((int)stream.Length);
        stream.CopyTo(buffer);
        var text = TextDecoding.Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), ReplacingUtf8);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
