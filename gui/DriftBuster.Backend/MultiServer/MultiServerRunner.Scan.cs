using System.Security.Cryptography;
using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.MultiServer;

/// <summary>The per-host scan: <c>_collect_secret_hits</c> and <c>_scan_plan</c>.</summary>
public sealed partial class MultiServerRunner
{
    private static readonly UTF8Encoding ReplacingUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// <c>_collect_secret_hits</c>: the absolute paths of every file the default hunt rules hit under each root. A root that
    /// does not exist is skipped; one that cannot be looked up or listed raises <see cref="DetectorIOException"/> for the root.
    /// Unreadable files are skipped by the hunt itself.
    /// </summary>
    internal static IReadOnlySet<string> CollectSecretHits(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var secretPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HuntScanResult result;
            try
            {
                result = HuntEngine.HuntPath(root, HuntRules.Default, "**/*", cancellationToken: cancellationToken);
            }
            catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                throw new DetectorIOException(root, exc.Message, exc);
            }

            foreach (var hit in result.Hits)
            {
                if (!string.IsNullOrEmpty(hit.Path))
                {
                    secretPaths.Add(Path.GetFullPath(hit.Path));
                }
            }
        }

        return secretPaths;
    }

    /// <summary>
    /// <c>_scan_plan</c>: the detector's budget is shared by every root of the host; each detected file becomes a record whose
    /// canonical payload comes from the cache when the entry's signature (host, config, root fingerprint, file hash) matches.
    /// </summary>
    private PlanScan ScanPlanCore(MultiServerPlan plan, IReadOnlyList<string> roots, IReadOnlySet<string> secretPaths, CancellationToken cancellationToken)
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

                var built = BuildRecord(plan, root, rootPositions[rootIndex], fingerprint, path, match, secretPaths, configs, cancellationToken);
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
                if (string.Equals(EnginePurePath.Str(plan.Roots[candidate]), roots[index], StringComparison.Ordinal))
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
        IReadOnlySet<string> secretPaths,
        OrderedDictionary<string, ConfigRecord> configs,
        CancellationToken cancellationToken)
    {
        var relative = EnginePurePath.RelativeTo(EnginePath.Absolute(path), EnginePath.Absolute(root)) ?? PathText.Name(path);
        var metadata = match.Metadata ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var catalogFormat = metadata.TryGetValue("catalog_format", out var format) ? format : null;
        var formatId = ConfigIdentity.IsTruthy(catalogFormat)
            ? EngineRepr.Str(catalogFormat)
            : string.IsNullOrEmpty(match.FormatName) ? "unknown" : match.FormatName;
        var contentType = ContentTypeResolver.FromCatalogFormat(catalogFormat as string);
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
        var signature = Sha256Hex($"{plan.HostId}:{configId}:{fingerprint}:{fileHash}");
        var cached = Cache.Load(plan.HostId, configId, signature);
        string canonical;
        if (cached is not null && cached.Count > 0)
        {
            canonical = cached.TryGetValue("canonical", out var stored) && ConfigIdentity.IsTruthy(stored) ? EngineRepr.Str(stored) : string.Empty;
        }
        else
        {
            canonical = Canonicaliser.Canonicalise(rawText, contentType);
            var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["canonical"] = canonical,
                ["content_type"] = contentType,
                ["metadata"] = DetectionMetadata.JsonSafe(metadata),
                ["file_hash"] = fileHash,
            };
            Cache.Save(plan.HostId, configId, signature, payload, cancellationToken);
        }

        var record = new ConfigRecord
        {
            ConfigId = configId,
            DisplayName = ConfigIdentity.DisplayName(metadata, relative),
            FormatId = formatId,
            ContentType = contentType,
            Canonical = canonical,
            Raw = rawText,
            Metadata = metadata,
            FileHash = fileHash,
            // os.path.abspath(path) on both sides: ".." removed lexically, as the hunt's hit paths were.
            Secrets = secretPaths.Contains(Path.GetFullPath(path)),
            Masked = metadata.TryGetValue("has_masked_tokens", out var maskedValue) && ConfigIdentity.IsTruthy(maskedValue),
            SourcePath = path,
            PluginName = string.IsNullOrEmpty(match.PluginName) ? "unknown" : match.PluginName,
            RelativePath = relative,
        };
        return (record, cached is not null && cached.Count > 0);
    }

    // path.is_file(), where a lookup error means the file cannot be read rather than failing the host.
    private static bool IsRegularFile(string path)
    {
        try
        {
            return EnginePath.IsFile(path);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>sha1("|".join(sorted(str(root.resolve()) for root in roots)))</c>: each root resolved as the OS resolves it, so a ".."
    /// after a symlink steps to the parent of the link's target.
    /// </summary>
    internal static string RootFingerprint(IReadOnlyList<string> roots)
    {
        var resolved = roots.Select(root => EnginePath.ResolvePhysicalPath(EnginePath.Absolute(root)) ?? Path.GetFullPath(root)).ToList();
        resolved.Sort(PathText.CompareCodePoints);
        return MultiServerPlan.Sha1Hex(string.Join('|', resolved));
    }

    /// <summary>
    /// <c>path.read_text(encoding="utf-8", errors="replace")</c>: the whole file, universal newlines. A file longer than
    /// <paramref name="maxBytes"/> raises <see cref="IOException"/> without being read: past <see cref="DefaultMaxTextBytes"/> its
    /// text may not fit a runtime string.
    /// </summary>
    internal static string ReadText(string path, long maxBytes = DefaultMaxTextBytes)
    {
        using var stream = new FileStream(EnginePath.KernelPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maxBytes)
        {
            throw new IOException($"File is larger than the {maxBytes} bytes the scan reads whole: '{path}'");
        }

        using var buffer = new MemoryStream((int)stream.Length);
        stream.CopyTo(buffer);
        var text = ReplacingUtf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
