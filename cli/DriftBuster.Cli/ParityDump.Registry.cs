using System.CommandLine;
using System.Numerics;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Cli;

/// <summary>The <c>registry-scan</c> surface of <c>parity-dump</c> (py_dump.py <c>cmd_registry_scan</c>).</summary>
public static partial class ParityDump
{
    private static readonly HashSet<string> RegistryRepeatableOptions = new(StringComparer.Ordinal) { "--keyword", "--pattern", "--root", "--remote-target" };

    /// <summary>
    /// py_dump.py <c>_registry_backend</c>: the first key whose hive and path equal the lookup and whose <c>view</c> (default <c>"*"</c>) is
    /// <c>"*"</c> or the requested view answers; <c>denied</c> lists nothing; <c>error</c> raises <see cref="PythonOSError.Create(int)"/> for
    /// <c>{"errno"}</c>, or <c>RuntimeError</c> / <c>ValueError</c> for <c>{"type", "message"}</c>, from <c>EnumValues</c> (and
    /// <c>EnumSubkeys</c> when <c>error_on</c> is <c>"subkeys"</c> or <c>"both"</c>).
    /// </summary>
    private sealed class CaseRegistryBackend(IReadOnlyList<object?> tree) : IRegistryBackend
    {
        public IReadOnlyList<string> EnumSubkeys(string hive, string path, string? view)
        {
            if (Find(hive, path, view) is not { } key || PythonBuiltins.IsTruthy(key.GetValueOrDefault("denied")))
            {
                return [];
            }

            Raise(key, "subkeys");
            return PythonBuiltins.Iterate(key.GetValueOrDefault("subkeys") ?? new List<object?>()).Select(name => (string)name!).ToList();
        }

        public IReadOnlyList<KeyValuePair<string, object?>> EnumValues(string hive, string path, string? view)
        {
            if (Find(hive, path, view) is not { } key || PythonBuiltins.IsTruthy(key.GetValueOrDefault("denied")))
            {
                return [];
            }

            Raise(key, "values");
            return PythonBuiltins.Iterate(key.GetValueOrDefault("values") ?? new List<object?>())
                .Select(entry => (List<object?>)entry!)
                .Select(entry => new KeyValuePair<string, object?>((string)entry[0]!, RegistryDecode(entry[1])))
                .ToList();
        }

        private IReadOnlyDictionary<string, object?>? Find(string hive, string path, string? view) => tree
            .Cast<IReadOnlyDictionary<string, object?>>()
            .FirstOrDefault(key => Equals(key["hive"], hive) && Equals(key["path"], path) && (key.GetValueOrDefault("view", "*") is "*" || Equals(key.GetValueOrDefault("view", "*"), view)));

        private static void Raise(IReadOnlyDictionary<string, object?> key, string operation)
        {
            var on = (string?)key.GetValueOrDefault("error_on") ?? "values";
            if (key.GetValueOrDefault("error") is not IReadOnlyDictionary<string, object?> error || (!string.Equals(on, operation, StringComparison.Ordinal) && !string.Equals(on, "both", StringComparison.Ordinal)))
            {
                return;
            }

            if (error.TryGetValue("errno", out var errno))
            {
                throw PythonOSError.Create((int)PythonBuiltins.Int(errno));
            }

            var message = (string)error["message"]!;
            throw error["type"] is "ValueError" ? new PythonValueException(message, nameof(key)) : new InvalidOperationException(message);
        }
    }

    private static Command BuildRegistryScan()
    {
        var caseArgument = new Argument<string>("case") { Description = "registry-scan case JSON file." };
        var command = new Command("registry-scan", "Registry operations, schema readers and registry_cli commands over a fake registry.");
        command.Arguments.Add(caseArgument);
        command.SetAction(parseResult =>
        {
            WriteLines(parseResult, [RegistryScanCase(parseResult.GetValue(caseArgument)!)]);
            return 0;
        });
        return command;
    }

    // py_dump.py _registry_decode: {"$bytes": hex} is a byte array, containers are walked.
    private static object? RegistryDecode(object? value) => value switch
    {
        IReadOnlyDictionary<string, object?> { Count: 1 } map when map.TryGetValue("$bytes", out var hex) => Convert.FromHexString((string)hex!),
        IReadOnlyDictionary<string, object?> map => new OrderedDictionary<string, object?>(
            map.Select(pair => new KeyValuePair<string, object?>(pair.Key, RegistryDecode(pair.Value))), StringComparer.Ordinal),
        List<object?> list => list.Select(RegistryDecode).ToList(),
        _ => value,
    };

    /// <summary>
    /// The case's fake registry through <see cref="RegistryOperations"/>: <c>enumerate</c>, <c>find_roots</c>, <c>searches</c>,
    /// <c>descriptors</c>, <c>remote_targets</c>, <c>scan_sources</c>, <c>remote_target_args</c>, <c>cli</c> (the <c>registry_cli</c>
    /// commands over <see cref="RegistryCommands"/> with the Windows gate open and the registry calls on the fake) and <c>usage</c>
    /// (<see cref="RegistryOperations.RegistrySummary"/> without durations and timestamps), as py_dump.py prints them.
    /// </summary>
    internal static string RegistryScanCase(string casePath)
    {
        var testCase = LoadCase(Path.GetFullPath(casePath));
        var backend = new CaseRegistryBackend(Items(testCase, "tree"));
        var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        IReadOnlyList<RegistryApp> apps = [];
        if (PythonBuiltins.IsTruthy(testCase.GetValueOrDefault("enumerate", true)))
        {
            record["enumerate"] = RegistryOutcome(() =>
            {
                apps = RegistryOperations.EnumerateInstalledApps(backend);
                return apps.Select(object? (app) => AppMap(app)).ToList();
            });
        }

        record["find_roots"] = Items(testCase, "find_roots").Select(object? (item) => RegistryOutcome(() =>
        {
            var entry = (IReadOnlyDictionary<string, object?>)item!;
            var installed = entry.GetValueOrDefault("installed", "enumerated") is "enumerated"
                ? apps
                : PythonBuiltins.Iterate(entry["installed"]).Select(app => AppFrom((IReadOnlyDictionary<string, object?>)app!)).ToList();
            return RegistryOperations.FindAppRegistryRoots((string)entry["token"]!, installed).Select(RootList).ToList();
        })).ToList();
        record["searches"] = Items(testCase, "searches").Select(object? (item) => RegistryOutcome(() => Search((IReadOnlyDictionary<string, object?>)item!, apps, backend))).ToList();
        record["descriptors"] = Items(testCase, "descriptors").Select(object? (text) => RegistryOutcome(() => RootList(RegistryRoot.Parse((string?)text)))).ToList();
        record["remote_targets"] = Items(testCase, "remote_targets").Select(object? (payload) => RegistryOutcome(() => TargetMap(RemoteRegistryTarget.FromPayload(payload)))).ToList();
        record["scan_sources"] = Items(testCase, "scan_sources").Select(object? (payload) => RegistryOutcome(() =>
        {
            var source = OfflineRegistryScanSource.FromDict((IReadOnlyDictionary<string, object?>)payload!);
            return new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["source"] = SourceMap(source), ["destination_name"] = source.DestinationName(fallbackIndex: 1) };
        })).ToList();
        record["remote_target_args"] = Items(testCase, "remote_target_args").Select(object? (text) => RegistryOutcome(() => RegistryCommands.ParseRemoteTargetArg((string)text!))).ToList();
        record["cli"] = Items(testCase, "cli").Select(object? (argv) => RegistryCliRun(PythonBuiltins.Iterate(argv).Select(arg => (string)arg!).ToList(), backend)).ToList();
        record["usage"] = RegistryOperations.RegistrySummary()
            .Select(object? (entry) => new OrderedDictionary<string, object?>(
                new[] { "operation", "calls", "successes", "errors", "last_error" }.Select(key => KeyValuePair.Create(key, entry[key])), StringComparer.Ordinal))
            .ToList();
        return Keyed(record);
    }

    private static List<object?> Search(IReadOnlyDictionary<string, object?> entry, IReadOnlyList<RegistryApp> apps, IRegistryBackend backend)
    {
        var roots = entry["roots"] is string token
            ? RegistryOperations.FindAppRegistryRoots(token, apps)
            : PythonBuiltins.Iterate(entry["roots"]).Select(root => PythonBuiltins.Iterate(root).ToList()).Select(root => new RegistryRoot((string)root[0]!, (string)root[1]!, (string?)root[2])).ToList();
        // _registry_spec: keywords and compiled patterns as SearchSpec holds them; the limits as the case gives them, converted inside
        // the instrumented search as search_registry converts them (int(max_depth), int(max_hits), float(time_budget_s), in that order).
        var spec = new SearchSpec
        {
            Keywords = entry.TryGetValue("keywords", out var keywords) ? Strings(keywords) : [],
            Patterns = entry.TryGetValue("patterns", out var patterns) ? Strings(patterns).Select(RegistryPython.Compile).ToList() : [],
        };
        SearchSpec Coerced()
        {
            if (entry.TryGetValue("max_depth", out var depth))
            {
                spec = spec with { MaxDepth = PythonBuiltins.Int(depth) };
            }

            if (entry.TryGetValue("max_hits", out var hits))
            {
                spec = spec with { MaxHits = PythonBuiltins.Int(hits) };
            }

            if (entry.TryGetValue("time_budget_s", out var budget))
            {
                spec = spec with { TimeBudgetS = PythonBuiltins.Float(budget) };
            }

            return spec;
        }

        return RegistryOperations.SearchRegistry(roots, Coerced, backend).Select(object? (hit) => HitMap(hit)).ToList();
    }

    // {"result": ...} or {"error": {"type", "message"}}, the type as RegistryPython names the Python exception.
    private static OrderedDictionary<string, object?> RegistryOutcome(Func<object?> produce)
    {
        try
        {
            return new(StringComparer.Ordinal) { ["result"] = produce() };
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            return new(StringComparer.Ordinal) { ["error"] = RegistryError(exc) };
        }
    }

    private static OrderedDictionary<string, object?> RegistryError(Exception exc) => new(StringComparer.Ordinal)
    {
        ["type"] = RegistryPython.ErrorName(exc),
        ["message"] = exc.Message,
    };

    /// <summary>
    /// <c>registry_cli.main(argv)</c> with <c>is_windows</c> true, <c>enumerate_installed_apps</c> and <c>search_registry</c> on
    /// <paramref name="backend"/> (<c>find_app_registry_roots</c> as shipped): <c>exit_code</c> or <c>error</c>, then <c>stdout</c> (every line
    /// printed before the command returned or raised). Arguments are the parser's: a subcommand, its token, then options each followed by
    /// its value.
    /// </summary>
    private static OrderedDictionary<string, object?> RegistryCliRun(IReadOnlyList<string> argv, IRegistryBackend backend)
    {
        var originals = (RegistryCommands.IsWindows, RegistryCommands.EnumerateInstalledApps, RegistryCommands.SearchRegistry);
        RegistryCommands.IsWindows = () => true;
        RegistryCommands.EnumerateInstalledApps = () => RegistryOperations.EnumerateInstalledApps(backend);
        RegistryCommands.SearchRegistry = (roots, spec) => RegistryOperations.SearchRegistry(roots, spec, backend);
        var run = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var stdout = new List<string>();
        try
        {
            stdout.AddRange(RegistryCliLines(argv));
            run["exit_code"] = 0;
        }
        catch (Exception exc) when (exc is not (OutOfMemoryException or InvalidDataException))
        {
            run["error"] = RegistryError(exc);
        }
        finally
        {
            (RegistryCommands.IsWindows, RegistryCommands.EnumerateInstalledApps, RegistryCommands.SearchRegistry) = originals;
        }

        run["stdout"] = string.Concat(stdout.Select(line => line + "\n"));
        return run;
    }

    private static readonly Dictionary<string, string[]> RegistryCliOptions = new(StringComparer.Ordinal)
    {
        ["list-apps"] = [],
        ["suggest-roots"] = [],
        ["search"] = ["--keyword", "--pattern", "--max-depth", "--max-hits", "--time-budget", "--root"],
        ["emit-config"] = ["--alias", "--keyword", "--pattern", "--max-depth", "--max-hits", "--time-budget", "--remote-target", "--root"],
    };

    /// <summary>
    /// The argv subset a <c>cli</c> record may use, the forms <c>registry_cli</c>'s argparse parser and <see cref="RegistryCliLines"/> read
    /// identically (phase 8 ports the parser itself): a subcommand first; then, in any order, one positional token (none for
    /// <c>list-apps</c>) and <c>--option value</c> pairs from that subcommand's options, each option spelled in full (no <c>--option=value</c>,
    /// no abbreviation, no <c>-h</c>); a value or token never begins with <c>-</c> unless it is argparse's negative number
    /// (<c>-digits</c> or <c>-digits.digits</c>, ASCII, which argparse reads as a value or positional since no option looks like one);
    /// <c>--max-depth</c> and <c>--max-hits</c> values convert with <c>int()</c> and
    /// <c>--time-budget</c> with <c>float()</c>. Anything else is refused here, so the dump exits with the reason instead of recording a
    /// result argparse would not have produced.
    /// </summary>
    internal static void RequireRegistryCliSubset(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        InvalidDataException Refuse(string why) => new($"registry-scan cli argv outside the documented subset ({why}): {string.Join(' ', argv)}");
        if (argv.Count == 0 || !RegistryCliOptions.TryGetValue(argv[0], out var options))
        {
            throw Refuse("unknown subcommand");
        }

        var positionals = 0;
        for (var index = 1; index < argv.Count; index++)
        {
            var item = argv[index];
            if (!item.StartsWith('-') || IsArgparseNegativeNumber(item))
            {
                positionals++;
                continue;
            }

            if (Array.IndexOf(options, item) < 0)
            {
                throw Refuse($"'{item}' is not an option of {argv[0]}");
            }

            if (++index >= argv.Count)
            {
                throw Refuse($"{item} without a value");
            }

            var value = argv[index];
            if (value.StartsWith('-') && !IsArgparseNegativeNumber(value))
            {
                throw Refuse($"{item} value '{value}' would be read as an option");
            }

            try
            {
                _ = item switch
                {
                    "--max-depth" or "--max-hits" => (object)PythonBuiltins.Int(value),
                    "--time-budget" => PythonBuiltins.Float(value),
                    _ => value,
                };
            }
            catch (PythonValueException exc)
            {
                throw Refuse($"{item} value '{value}' does not convert: {exc.Message}");
            }
        }

        var expected = string.Equals(argv[0], "list-apps", StringComparison.Ordinal) ? 0 : 1;
        if (positionals != expected)
        {
            throw Refuse($"{positionals} positional argument(s), {expected} expected");
        }
    }

    // argparse's _negative_number_matcher, ^-\d+$|^-\d*\.\d+$, over ASCII digits.
    private static bool IsArgparseNegativeNumber(string value)
    {
        var digits = value.AsSpan(1);
        var dot = digits.IndexOf('.');
        if (dot < 0)
        {
            return digits.Length > 0 && !digits.ContainsAnyExcept("0123456789");
        }

        var fraction = digits[(dot + 1)..];
        return fraction.Length > 0 && !digits[..dot].ContainsAnyExcept("0123456789") && !fraction.ContainsAnyExcept("0123456789");
    }

    private static IReadOnlyList<string> RegistryCliLines(IReadOnlyList<string> argv)
    {
        RequireRegistryCliSubset(argv);
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (var index = 1; index < argv.Count; index++)
        {
            if (argv[index].StartsWith("--", StringComparison.Ordinal))
            {
                var values = options.TryGetValue(argv[index], out var existing) ? existing : options[argv[index]] = [];
                if (!RegistryRepeatableOptions.Contains(argv[index]))
                {
                    values.Clear();
                }

                values.Add(argv[++index]);
            }
            else
            {
                positional.Add(argv[index]);
            }
        }

        List<string> Many(string name) => options.GetValueOrDefault(name, []);
        string? One(string name) => options.TryGetValue(name, out var values) ? values[^1] : null;
        BigInteger? Int(string name) => One(name) is { } text ? PythonBuiltins.Int(text) : null;
        var budget = One("--time-budget") is { } seconds ? PythonBuiltins.Float(seconds) : 10.0;
        return argv[0] switch
        {
            "list-apps" => RegistryCommands.ListApps(),
            "suggest-roots" => RegistryCommands.SuggestRoots(positional[0]),
            "search" => RegistryCommands.Search(positional[0], Many("--keyword"), Many("--pattern"), Int("--max-depth"), Int("--max-hits"), budget, Many("--root")),
            _ => [RegistryCommands.EmitConfigJson(RegistryCommands.EmitConfig(
                positional[0], One("--alias"), Many("--keyword"), Many("--pattern"), Int("--max-depth"), Int("--max-hits"), budget, Many("--remote-target"), Many("--root")))],
        };
    }

    private static List<object?> RootList(RegistryRoot root) => [root.Hive, root.Path, root.View];

    private static object? NarrowInt(BigInteger? value) => value is { } number ? PythonValues.Narrow(number) : null;

    private static RegistryApp AppFrom(IReadOnlyDictionary<string, object?> map) => new(
        (string)map["display_name"]!,
        (string)map["key_path"]!,
        (string)map["hive"]!,
        (string?)map.GetValueOrDefault("publisher"),
        (string?)map.GetValueOrDefault("version"),
        (string?)map.GetValueOrDefault("uninstall_string"),
        (string?)map.GetValueOrDefault("install_location"),
        (string?)map.GetValueOrDefault("view") ?? "auto");

    private static OrderedDictionary<string, object?> AppMap(RegistryApp app) => new(StringComparer.Ordinal)
    {
        ["display_name"] = app.DisplayName,
        ["key_path"] = app.KeyPath,
        ["hive"] = app.Hive,
        ["publisher"] = app.Publisher,
        ["version"] = app.Version,
        ["uninstall_string"] = app.UninstallString,
        ["install_location"] = app.InstallLocation,
        ["view"] = app.View,
    };

    private static OrderedDictionary<string, object?> HitMap(RegistryHit hit) => new(StringComparer.Ordinal)
    {
        ["path"] = hit.Path,
        ["hive"] = hit.Hive,
        ["value_name"] = hit.ValueName,
        ["data_preview"] = hit.DataPreview,
        ["reason"] = hit.Reason,
    };

    private static OrderedDictionary<string, object?> TargetMap(RemoteRegistryTarget target) => new(StringComparer.Ordinal)
    {
        ["host"] = target.Host,
        ["transport"] = target.Transport,
        ["port"] = NarrowInt(target.Port),
        ["use_ssl"] = target.UseSsl,
        ["username"] = target.Username,
        ["password_env"] = target.PasswordEnv,
        ["credential_profile"] = target.CredentialProfile,
        ["alias"] = target.Alias,
    };

    private static OrderedDictionary<string, object?> SourceMap(OfflineRegistryScanSource source) => new(StringComparer.Ordinal)
    {
        ["token"] = source.Token,
        ["keywords"] = source.Keywords.Cast<object?>().ToList(),
        ["patterns"] = source.Patterns.Cast<object?>().ToList(),
        ["max_depth"] = NarrowInt(source.MaxDepth),
        ["max_hits"] = NarrowInt(source.MaxHits),
        ["time_budget_s"] = source.TimeBudgetS,
        ["alias"] = source.Alias,
        ["remote"] = source.Remote is null ? null : TargetMap(source.Remote),
        ["remote_batch"] = source.RemoteBatch.Select(object? (target) => TargetMap(target)).ToList(),
        ["roots"] = source.Roots.Select(object? (root) => new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["hive"] = root.Hive,
            ["path"] = root.Path,
            ["view"] = root.View,
        }).ToList(),
    };
}
