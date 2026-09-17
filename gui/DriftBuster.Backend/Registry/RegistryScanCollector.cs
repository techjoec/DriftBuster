using System.Security.Cryptography;
using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// The <c>registry_scan</c> branch of <c>offline_runner.execute_config</c>: runs one <see cref="OfflineRegistryScanSource"/> into
/// its destination directory and returns the manifest source summary with the file it wrote. The registry calls are looked up
/// through settable seams at call time, so tests can swap them.
/// </summary>
public static class RegistryScanCollector
{
    internal const string ResultFileName = "registry_scan.json";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    internal static Func<bool> IsWindows { get; set; } = RegistryScan.PlatformIsWindows;

    internal static Func<IReadOnlyList<RegistryApp>> EnumerateInstalledApps { get; set; } = () => RegistryOperations.EnumerateInstalledApps();

    internal static Func<string, IReadOnlyList<RegistryApp>, IReadOnlyList<RegistryRoot>> FindAppRegistryRoots { get; set; }
        = (token, installed) => RegistryOperations.FindAppRegistryRoots(token, installed);

    internal static Func<IReadOnlyList<RegistryRoot>, SearchSpec, IReadOnlyList<RegistryHit>> SearchRegistry { get; set; }
        = (roots, spec) => RegistryOperations.SearchRegistry(roots, spec);

    /// <summary>
    /// Off Windows: logs <c>registry scan skipped: non-Windows platform</c> and returns the skipped summary (<c>reason</c>
    /// <c>not-windows</c>) with no file. Otherwise searches the source's explicit roots, or the roots suggested for its token from
    /// the installed applications, with its keywords, compiled patterns and limits; writes <see cref="ResultFileName"/> under
    /// <paramref name="destinationRoot"/> as <c>json.dumps(payload, indent=2)</c> in text mode; and returns the summary with the
    /// file's size and SHA-256.
    /// </summary>
    /// <exception cref="System.Text.RegularExpressions.RegexParseException">A pattern is not a valid .NET regular expression.</exception>
    public static RegistryScanCollection Collect(OfflineRegistryScanSource source, string destinationRoot, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinationRoot);
        log ??= _ => { };
        if (!IsWindows())
        {
            log("registry scan skipped: non-Windows platform");
            var skipped = Summary(source);
            skipped["skipped"] = true;
            skipped["reason"] = "not-windows";
            return new RegistryScanCollection(skipped, null, 0, null);
        }

        log($"registry scan started for token: {source.Token}");
        var roots = source.Roots.Count > 0 ? source.Roots : FindAppRegistryRoots(source.Token, EnumerateInstalledApps());
        var spec = new SearchSpec
        {
            Keywords = source.Keywords,
            Patterns = source.Patterns.Select(RegistryText.Compile).ToList(),
            MaxDepth = source.MaxDepth,
            MaxHits = source.MaxHits,
            TimeBudgetS = source.TimeBudgetS,
        };
        var hits = SearchRegistry(roots, spec);

        var resultPath = LexicalPath.Join(destinationRoot, ResultFileName);
        var text = Canonicaliser.Dumps(ResultPayload(source, roots, hits), indent: true, ensureAscii: true, sortKeys: false);
        if (!string.Equals(Environment.NewLine, "\n", StringComparison.Ordinal))
        {
            text = text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        }

        var bytes = Utf8.GetBytes(text);
        EngineTextFile.WriteBytes(resultPath, bytes);

        var summary = Summary(source);
        summary["roots"] = roots.Select(root => (object?)$"{root.Hive} \\ {root.Path}").ToList();
        summary["hits"] = hits.Count;
        summary["output"] = OperatingSystem.IsWindows() ? resultPath.Replace('\\', '/') : resultPath;
        if (source.Roots.Count > 0)
        {
            summary["requested_roots"] = source.Roots
                .Select(root => (object?)(root.View is null ? $"{root.Hive} \\ {root.Path}" : $"{root.Hive} \\ {root.Path} (view {root.View})"))
                .ToList();
        }

        return new RegistryScanCollection(summary, resultPath, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static OrderedDictionary<string, object?> Summary(OfflineRegistryScanSource source) => new(StringComparer.Ordinal)
    {
        ["type"] = "registry_scan",
        ["token"] = source.Token,
        ["keywords"] = source.Keywords.Cast<object?>().ToList(),
        ["patterns"] = source.Patterns.Cast<object?>().ToList(),
    };

    /// <summary>The <c>registry_scan.json</c> payload: token, keywords, patterns, searched roots, hits and, for explicit roots, the requested roots.</summary>
    internal static OrderedDictionary<string, object?> ResultPayload(
        OfflineRegistryScanSource source,
        IReadOnlyList<RegistryRoot> roots,
        IReadOnlyList<RegistryHit> hits)
    {
        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["token"] = source.Token,
            ["keywords"] = source.Keywords.Cast<object?>().ToList(),
            ["patterns"] = source.Patterns.Cast<object?>().ToList(),
            ["roots"] = roots.Select(RootPayload).ToList(),
            ["hits"] = hits.Select(hit => (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["hive"] = hit.Hive,
                ["path"] = hit.Path,
                ["value_name"] = hit.ValueName,
                ["data_preview"] = hit.DataPreview,
                ["reason"] = hit.Reason,
            }).ToList(),
        };
        if (source.Roots.Count > 0)
        {
            payload["requested_roots"] = source.Roots.Select(RootPayload).ToList();
        }

        return payload;
    }

    private static object? RootPayload(RegistryRoot root) => new OrderedDictionary<string, object?>(StringComparer.Ordinal)
    {
        ["hive"] = root.Hive,
        ["path"] = root.Path,
        ["view"] = root.View,
    };
}
