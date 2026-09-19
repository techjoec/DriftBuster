
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// <c>driftbuster registry-scan</c> as library calls: the Windows gate, <c>--root</c> and <c>--remote-target</c> parsing, and
/// <c>list-apps</c>, <c>suggest-roots</c>, <c>search</c>, <c>emit-config</c>, each returning the lines or payload to print. Every command
/// checks the gate first; all but <c>list-apps</c> then enumerate installed apps. The registry calls are test seams.
/// </summary>
public static class RegistryCommands
{

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
        int maxDepth = 12,
        int maxHits = 200,
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
            MaxDepth = maxDepth,
            MaxHits = maxHits,
            TimeBudgetS = timeBudget,
        };
        return SearchRegistry(searchRoots, spec)
            .Select(hit => $"{hit.Hive} \\ {hit.Path} :: {hit.ValueName} = {hit.DataPreview}")
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// A <c>registry_scan</c> source for an offline runner config: the token, non-empty keywords and patterns, limits, explicit roots
    /// (<c>HIVE\path[,view=32|64]</c>) and remote targets (<see cref="RegistryRemoteTarget.Parse"/>; the first is <c>remote</c>, the rest
    /// <c>remote_batch</c>).
    /// </summary>
    public static RegistryScanConfig EmitConfig(
        string token,
        string? alias = null,
        IReadOnlyList<string>? keywords = null,
        IReadOnlyList<string>? patterns = null,
        int maxDepth = 12,
        int maxHits = 200,
        double timeBudget = 10.0,
        IReadOnlyList<string>? remoteTargets = null,
        IReadOnlyList<string>? roots = null)
    {
        RequireWindows();
        var explicitRoots = ParseRootArguments(roots ?? []);
        List<RegistryRemoteTarget> targets;
        try
        {
            targets = [.. (remoteTargets ?? []).Select(RegistryRemoteTarget.Parse)];
        }
        catch (FormatException exc)
        {
            throw new CommandExitException($"invalid --remote-target value: {exc.Message}", exc);
        }

        return new RegistryScanConfig(
            new RegistryScanSpec(
                token,
                [.. (keywords ?? []).Where(keyword => keyword.Length > 0)],
                [.. (patterns ?? []).Where(pattern => pattern.Length > 0)],
                maxDepth,
                maxHits,
                timeBudget,
                explicitRoots.Count > 0 ? [.. explicitRoots.Select(root => new RegistryRootEntrySpec(root.Hive, root.Path, string.IsNullOrEmpty(root.View) ? null : root.View))] : null,
                targets.Count > 0 ? targets[0] : null,
                targets.Count > 1 ? targets[1..] : null),
            string.IsNullOrEmpty(alias) ? null : alias);
    }

    private static List<RegistryRoot> ParseRootArguments(IReadOnlyList<string> values)
    {
        try
        {
            return [.. values.Select(RegistryRoot.Parse)];
        }
        catch (FormatException exc)
        {
            throw new CommandExitException($"invalid --root value: {exc.Message}", exc);
        }
    }
}
