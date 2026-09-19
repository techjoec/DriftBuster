using System.Numerics;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// <c>driftbuster registry-scan</c> as library calls: the Windows gate, <c>--root</c> and <c>--remote-target</c> parsing, and
/// <c>list-apps</c>, <c>suggest-roots</c>, <c>search</c>, <c>emit-config</c>, each returning the lines or payload to print. Every command
/// checks the gate first; all but <c>list-apps</c> then enumerate installed apps. The registry calls are test seams.
/// </summary>
public static class RegistryCommands
{
    private static readonly string[] TrueWords = ["1", "true", "yes", "on"];
    private static readonly string[] FalseWords = ["0", "false", "no", "off"];

    internal static Func<bool> IsWindows { get; set; } = RegistryScan.PlatformIsWindows;

    internal static Func<IReadOnlyList<RegistryApp>> EnumerateInstalledApps { get; set; } = () => RegistryOperations.EnumerateInstalledApps();

    internal static Func<string, IReadOnlyList<RegistryApp>, IReadOnlyList<RegistryRoot>> FindAppRegistryRoots { get; set; }
        = (token, installed) => RegistryOperations.FindAppRegistryRoots(token, installed);

    internal static Func<IReadOnlyList<RegistryRoot>, SearchSpec, IReadOnlyList<RegistryHit>> SearchRegistry { get; set; }
        = (roots, spec) => RegistryOperations.SearchRegistry(roots, spec);

    internal static Func<RegistryRoot, bool> RootExists { get; set; }
        = root => !OperatingSystem.IsWindows() || WinRegistryBackend.KeyExists(root.Hive, root.Path, root.View);

    /// <summary>Throws <c>Registry scanning requires Windows.</c> off Windows.</summary>
    /// <exception cref="CommandExitException">Not on Windows.</exception>
    public static void RequireWindows()
    {
        if (!IsWindows())
        {
            throw new CommandExitException("Registry scanning requires Windows.");
        }
    }

    /// <summary>One line per installed app: <c>"{display_name}[ {version}]  [{hive} {view}]  {key_path}"</c>.</summary>
    public static IReadOnlyList<string> ListApps()
    {
        RequireWindows();
        return EnumerateInstalledApps()
            .Select(app => $"{app.DisplayName}{(string.IsNullOrEmpty(app.Version) ? string.Empty : " " + app.Version)}  [{app.Hive} {app.View}]  {app.KeyPath}")
            .ToList()
            .AsReadOnly();
    }

    /// <summary>One line per suggested root: <c>"{hive} \ {path}[ ({view}-bit)]"</c>.</summary>
    public static IReadOnlyList<string> SuggestRoots(string token)
    {
        RequireWindows();
        var apps = EnumerateInstalledApps();
        return FindAppRegistryRoots(token, apps)
            .Select(root => $"{root.Hive} \\ {root.Path}{(root.View is "32" or "64" ? $" ({root.View}-bit)" : string.Empty)}")
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Searches the explicit <c>--root</c> values (a bad one throws <see cref="CommandExitException"/> <c>invalid --root value: ...</c>) or
    /// the roots suggested for the token (defaults: depth 12, 200 hits); one line per hit:
    /// <c>"{hive} \ {path} :: {value_name} = {data_preview}"</c>.
    /// </summary>
    /// <exception cref="System.Text.RegularExpressions.RegexParseException">A pattern is not a valid .NET regular expression.</exception>
    public static IReadOnlyList<string> Search(
        string token,
        IReadOnlyList<string>? keywords = null,
        IReadOnlyList<string>? patterns = null,
        BigInteger? maxDepth = null,
        BigInteger? maxHits = null,
        double timeBudget = 10.0,
        IReadOnlyList<string>? roots = null,
        Action<string>? warn = null)
    {
        RequireWindows();
        var apps = EnumerateInstalledApps();
        var explicitRoots = ParseRootArguments(roots ?? []);
        // A --root that does not open would otherwise read as "no hits".
        foreach (var missing in explicitRoots.Where(root => !RootExists(root)))
        {
            warn?.Invoke($"Registry key not found: {missing.Hive}\\{missing.Path}");
        }

        var searchRoots = explicitRoots.Count > 0 ? explicitRoots : FindAppRegistryRoots(token, apps);
        var spec = new SearchSpec
        {
            Keywords = (keywords ?? []).ToList(),
            Patterns = (patterns ?? []).Select(RegistryText.Compile).ToList(),
            MaxDepth = maxDepth ?? 12,
            MaxHits = maxHits ?? 200,
            TimeBudgetS = timeBudget,
        };
        return SearchRegistry(searchRoots, spec)
            .Select(hit => $"{hit.Hive} \\ {hit.Path} :: {hit.ValueName} = {hit.DataPreview}")
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// <c>{"registry_scan": {...}}</c>: token, non-empty keywords and patterns, limits, explicit roots (<c>{"hive","path"[,"view"]}</c>),
    /// the first <c>--remote-target</c> as <c>remote</c> and the rest as <c>remote_batch</c>, and <c>alias</c> when set.
    /// </summary>
    /// <exception cref="CommandExitException">A <c>--root</c> value is refused.</exception>
    /// <exception cref="FormatException">A <c>--remote-target</c> value is refused.</exception>
    public static OrderedDictionary<string, object?> EmitConfig(
        string token,
        string? alias = null,
        IReadOnlyList<string>? keywords = null,
        IReadOnlyList<string>? patterns = null,
        BigInteger? maxDepth = null,
        BigInteger? maxHits = null,
        double timeBudget = 10.0,
        IReadOnlyList<string>? remoteTargets = null,
        IReadOnlyList<string>? roots = null)
    {
        RequireWindows();
        _ = EnumerateInstalledApps();
        var scan = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["token"] = token,
            ["keywords"] = (keywords ?? []).Where(keyword => keyword.Length > 0).Cast<object?>().ToList(),
            ["patterns"] = (patterns ?? []).Where(pattern => pattern.Length > 0).Cast<object?>().ToList(),
            ["max_depth"] = EngineValues.Narrow(maxDepth ?? 12),
            ["max_hits"] = EngineValues.Narrow(maxHits ?? 200),
            ["time_budget_s"] = timeBudget,
        };
        var snippet = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["registry_scan"] = scan };
        if (!string.IsNullOrEmpty(alias))
        {
            snippet["alias"] = alias;
        }

        var explicitRoots = ParseRootArguments(roots ?? []);
        if (explicitRoots.Count > 0)
        {
            scan["roots"] = explicitRoots.Select(RootEntry).ToList();
        }

        var targets = (remoteTargets ?? []).Select(ParseRemoteTargetArg).ToList();
        if (targets.Count > 0)
        {
            scan["remote"] = targets[0];
            if (targets.Count > 1)
            {
                scan["remote_batch"] = targets.Skip(1).Cast<object?>().ToList();
            }
        }

        foreach (var key in new[] { "keywords", "patterns" })
        {
            if (!EngineBuiltins.IsTruthy(scan[key]))
            {
                scan.Remove(key);
            }
        }

        return snippet;
    }

    /// <summary>Indented JSON with sorted keys.</summary>
    public static string EmitConfigJson(OrderedDictionary<string, object?> snippet)
        => Canonicaliser.DumpsSorted(snippet, indent: true, ensureAscii: true);

    private static object? RootEntry(RegistryRoot root)
    {
        var entry = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["hive"] = root.Hive, ["path"] = root.Path };
        if (!string.IsNullOrEmpty(root.View))
        {
            entry["view"] = root.View;
        }

        return entry;
    }

    private static List<RegistryRoot> ParseRootArguments(IReadOnlyList<string> values)
    {
        try
        {
            return values.Select(ParseRootArgument).ToList();
        }
        catch (FormatException exc)
        {
            throw new CommandExitException($"invalid --root value: {exc.Message}", exc);
        }
    }

    public static RegistryRoot ParseRootArgument(string value) => RegistryRoot.Parse(value);

    /// <summary>
    /// <c>HOST[,key=value]...</c> into <c>{"host": ...}</c> plus <c>port</c> (integer), <c>use_ssl</c> (1/true/yes/on or 0/false/no/off),
    /// <c>username</c> (or <c>user</c>), <c>password_env</c>, <c>credential_profile</c>, <c>transport</c>, <c>alias</c>; keys are trimmed,
    /// lower-cased and read with "-" as "_".
    /// </summary>
    /// <exception cref="FormatException">A refused key or value, or a non-integer port.</exception>
    public static OrderedDictionary<string, object?> ParseRemoteTargetArg(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = value.Split(',').Select(EngineText.Strip).Where(segment => segment.Length > 0).ToList();
        if (parts.Count == 0)
        {
            throw new FormatException("remote target requires a host segment");
        }

        if (parts[0].Contains('=', StringComparison.Ordinal))
        {
            throw new FormatException("remote target must start with the host name");
        }

        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["host"] = parts[0] };
        foreach (var entry in parts.Skip(1))
        {
            ApplyRemoteTargetEntry(payload, entry);
        }

        return payload;
    }

    private static void ApplyRemoteTargetEntry(OrderedDictionary<string, object?> payload, string entry)
    {
        var separator = entry.IndexOf('=', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new FormatException($"Remote target entry '{entry}' must include '='");
        }

        var key = entry[..separator];
        var normalised = EngineText.Lower(EngineText.Strip(key)).Replace('-', '_');
        var rawValue = EngineText.Strip(entry[(separator + 1)..]);
        if (rawValue.Length == 0)
        {
            throw new FormatException($"Remote target value for '{key}' must be non-empty");
        }

        switch (normalised)
        {
            case "port":
                payload["port"] = EngineValues.Narrow(EngineBuiltins.Int(rawValue));
                break;
            case "use_ssl":
                payload["use_ssl"] = ParseSwitch(rawValue);
                break;
            case "username" or "user":
                payload["username"] = rawValue;
                break;
            case "password_env" or "credential_profile" or "transport" or "alias":
                payload[normalised] = rawValue;
                break;
            default:
                throw new FormatException($"Unsupported remote target key '{key}'");
        }
    }

    private static bool ParseSwitch(string rawValue)
    {
        var lowered = EngineText.Lower(rawValue);
        if (TrueWords.Contains(lowered, StringComparer.Ordinal))
        {
            return true;
        }

        return FalseWords.Contains(lowered, StringComparer.Ordinal)
            ? false
            : throw new FormatException($"Unsupported boolean value '{rawValue}' for use-ssl");
    }
}
