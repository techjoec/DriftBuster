using System.Numerics;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// The library half of <c>registry_cli</c> (<c>driftbuster-registry</c>): the Windows gate, <c>--root</c> and
/// <c>--remote-target</c> parsing, and the <c>list-apps</c>, <c>suggest-roots</c>, <c>search</c> and <c>emit-config</c> commands, each
/// returning the lines or payload the command prints. Every command checks the gate first and, except <c>list-apps</c>, enumerates
/// the installed applications before anything else, as <c>main</c> does. Argument parsing and exit codes belong to the console tool.
/// The registry calls are settable seams, as tests monkeypatch the module attributes <c>main</c> reads.
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

    /// <summary><c>main</c>'s gate: <c>SystemExit("Registry scanning requires Windows.")</c> off Windows.</summary>
    /// <exception cref="CommandExitException">Not on Windows.</exception>
    public static void RequireWindows()
    {
        if (!IsWindows())
        {
            throw new CommandExitException("Registry scanning requires Windows.");
        }
    }

    /// <summary><c>list-apps</c>: <c>"{display_name}[ {version}]  [{hive} {view}]  {key_path}"</c> per installed application.</summary>
    public static IReadOnlyList<string> ListApps()
    {
        RequireWindows();
        return EnumerateInstalledApps()
            .Select(app => $"{app.DisplayName}{(string.IsNullOrEmpty(app.Version) ? string.Empty : " " + app.Version)}  [{app.Hive} {app.View}]  {app.KeyPath}")
            .ToList()
            .AsReadOnly();
    }

    /// <summary><c>suggest-roots TOKEN</c>: <c>"{hive} \ {path}[ ({view}-bit)]"</c> per suggested root.</summary>
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
    /// <c>search TOKEN</c> (<c>--max-depth</c> and <c>--max-hits</c> are <c>argparse</c> <c>int</c>s of any size, default 12 and 200): the explicit <c>--root</c> values (<see cref="ParseRootArgument"/>, a refusal becoming
    /// <c>SystemExit("invalid --root value: ...")</c>) or the roots suggested for the token, searched with the keywords, the patterns
    /// compiled in order and the limits; <c>"{hive} \ {path} :: {value_name} = {data_preview}"</c> per hit.
    /// </summary>
    /// <exception cref="PythonReException">A pattern does not compile (<c>re.error</c>).</exception>
    public static IReadOnlyList<string> Search(
        string token,
        IReadOnlyList<string>? keywords = null,
        IReadOnlyList<string>? patterns = null,
        BigInteger? maxDepth = null,
        BigInteger? maxHits = null,
        double timeBudget = 10.0,
        IReadOnlyList<string>? roots = null)
    {
        RequireWindows();
        var apps = EnumerateInstalledApps();
        var explicitRoots = ParseRootArguments(roots ?? []);
        var searchRoots = explicitRoots.Count > 0 ? explicitRoots : FindAppRegistryRoots(token, apps);
        var spec = new SearchSpec
        {
            Keywords = (keywords ?? []).ToList(),
            Patterns = (patterns ?? []).Select(RegistryPython.Compile).ToList(),
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
    /// <c>emit-config TOKEN</c>: <c>{"registry_scan": {...}}</c> with the token, the non-empty keywords and patterns (each list left
    /// out when empty), the limits, the explicit roots as <c>{"hive", "path"[, "view"]}</c> when given, and the first parsed
    /// <c>--remote-target</c> as <c>remote</c> with the rest as <c>remote_batch</c>; <c>alias</c> beside it when truthy. The command
    /// prints <see cref="EmitConfigJson"/> of it.
    /// </summary>
    /// <exception cref="CommandExitException">A <c>--root</c> value is refused.</exception>
    /// <exception cref="PythonValueException">A <c>--remote-target</c> value is refused (not converted to <c>SystemExit</c>).</exception>
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
            ["max_depth"] = PythonValues.Narrow(maxDepth ?? 12),
            ["max_hits"] = PythonValues.Narrow(maxHits ?? 200),
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
            if (!PythonBuiltins.IsTruthy(scan[key]))
            {
                scan.Remove(key);
            }
        }

        return snippet;
    }

    /// <summary><c>json.dumps(snippet, indent=2, sort_keys=True)</c>.</summary>
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
        catch (PythonValueException exc)
        {
            throw new CommandExitException($"invalid --root value: {exc.Message}", exc);
        }
    }

    /// <summary><c>_parse_root_argument(value)</c>: <see cref="RegistryRoot.Parse"/>.</summary>
    public static RegistryRoot ParseRootArgument(string value) => RegistryRoot.Parse(value);

    /// <summary>
    /// <c>_parse_remote_target_arg(value)</c>: <c>HOST[,key=value]...</c> into <c>{"host": ...}</c> plus <c>port</c> (<c>int()</c>),
    /// <c>use_ssl</c> (1/true/yes/on or 0/false/no/off), <c>username</c> (also <c>user</c>), <c>password_env</c>,
    /// <c>credential_profile</c>, <c>transport</c> and <c>alias</c>; keys are stripped, lower-cased and read with "-" as "_".
    /// </summary>
    /// <exception cref="PythonValueException">Python's <c>ValueError</c> text for each refusal, or from <c>int()</c>.</exception>
    public static OrderedDictionary<string, object?> ParseRemoteTargetArg(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = value.Split(',').Select(PythonText.Strip).Where(segment => segment.Length > 0).ToList();
        if (parts.Count == 0)
        {
            throw new PythonValueException("remote target requires a host segment", nameof(value));
        }

        if (parts[0].Contains('=', StringComparison.Ordinal))
        {
            throw new PythonValueException("remote target must start with the host name", nameof(value));
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
            throw new PythonValueException($"Remote target entry '{entry}' must include '='", nameof(entry));
        }

        var key = entry[..separator];
        var normalised = PythonText.Lower(PythonText.Strip(key)).Replace('-', '_');
        var rawValue = PythonText.Strip(entry[(separator + 1)..]);
        if (rawValue.Length == 0)
        {
            throw new PythonValueException($"Remote target value for '{key}' must be non-empty", nameof(entry));
        }

        switch (normalised)
        {
            case "port":
                payload["port"] = PythonValues.Narrow(PythonBuiltins.Int(rawValue));
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
                throw new PythonValueException($"Unsupported remote target key '{key}'", nameof(entry));
        }
    }

    private static bool ParseSwitch(string rawValue)
    {
        var lowered = PythonText.Lower(rawValue);
        if (TrueWords.Contains(lowered, StringComparer.Ordinal))
        {
            return true;
        }

        return FalseWords.Contains(lowered, StringComparer.Ordinal)
            ? false
            : throw new PythonValueException($"Unsupported boolean value '{rawValue}' for use-ssl", nameof(rawValue));
    }
}
