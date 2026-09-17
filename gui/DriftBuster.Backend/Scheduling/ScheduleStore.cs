using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Scheduling;

/// <summary>
/// The schedule files <c>run_profiles_cli</c> keeps under the profiles root: the <c>schedules.json</c> manifest
/// (<c>{"schedules": [...]}</c> or a bare array) and <c>scheduler-state.json</c> (per schedule <c>next_run</c> and <c>pending</c>,
/// written as <c>json.dumps(snapshot, indent=2, sort_keys=True)</c> plus a new line). The GUI's lenient manifest reading and its
/// normalising writer live in <see cref="ScheduleStore"/>'s <c>ListSchedules</c> / <c>SaveSchedules</c> half.
/// </summary>
public static partial class ScheduleStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary><c>_default_schedule_config_path(base_dir, override)</c>: the override as given, else <c>profiles_root(base_dir) / "schedules.json"</c> (creating the root).</summary>
    public static string DefaultConfigPath(string? baseDir, string? overridePath)
        => overridePath is not null ? EnginePurePath.Str(overridePath) : RunProfileStore.JoinName(RunProfileStore.ProfilesRoot(baseDir), "schedules.json");

    /// <summary><c>_default_schedule_state_path(base_dir, override)</c>: the override as given, else <c>profiles_root(base_dir) / "scheduler-state.json"</c>.</summary>
    public static string DefaultStatePath(string? baseDir, string? overridePath)
        => overridePath is not null ? EnginePurePath.Str(overridePath) : RunProfileStore.JoinName(RunProfileStore.ProfilesRoot(baseDir), "scheduler-state.json");

    /// <summary>
    /// <c>_load_schedule_payload(path)</c>: the mapping entries of the manifest's <c>schedules</c> (or of a bare array). A missing file, text
    /// that is not JSON and a truthy payload that is neither a list nor a str raise <see cref="CommandExitException"/>; a falsy payload is empty.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> LoadSchedulePayload(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!RunProfileStore.Exists(path))
        {
            throw new CommandExitException($"Schedule manifest not found: {path}");
        }

        var payload = ReadJson(path) ?? throw new CommandExitException($"Failed to parse schedules from {path}: invalid JSON document");
        var entries = payload.Value is IReadOnlyDictionary<string, object?> mapping
            ? mapping.TryGetValue("schedules", out var schedules) ? schedules : new List<object?>()
            : payload.Value;
        if (!EngineBuiltins.IsTruthy(entries))
        {
            return [];
        }

        return entries switch
        {
            List<object?> list => list.OfType<IReadOnlyDictionary<string, object?>>().ToList(),
            string => [],
            _ => throw new CommandExitException("Schedules payload must be an array of schedule entries."),
        };
    }

    /// <summary>
    /// <c>_build_schedule_specs(entries, base_dir=...)</c>: each entry through <see cref="ScheduleSpec.FromDict"/> with a loader over
    /// <see cref="RunProfileStore.LoadProfile"/>; a <see cref="ScheduleException"/> becomes a <see cref="CommandExitException"/>.
    /// </summary>
    public static IReadOnlyList<ScheduleSpec> BuildScheduleSpecs(IEnumerable<IReadOnlyDictionary<string, object?>> entries, string? baseDir)
    {
        ArgumentNullException.ThrowIfNull(entries);
        RunProfile Loader(string name) => RunProfileStore.LoadProfile(name, baseDir);
        var specs = new List<ScheduleSpec>();
        foreach (var entry in entries)
        {
            try
            {
                specs.Add(ScheduleSpec.FromDict(entry, Loader));
            }
            catch (ScheduleException exc)
            {
                throw new CommandExitException(exc.Message, exc);
            }
        }

        return specs;
    }

    /// <summary>
    /// <c>_load_schedule_state(path)</c>: empty when the file is missing; each mapping entry reduced to <c>next_run</c> and <c>pending</c>
    /// (entries that are not mappings skipped). Text that is not JSON, or a payload that is not an object, raises <see cref="CommandExitException"/>.
    /// </summary>
    public static OrderedDictionary<string, object?> LoadScheduleState(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalised = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        if (!RunProfileStore.Exists(path))
        {
            return normalised;
        }

        var payload = ReadJson(path) ?? throw new CommandExitException($"Failed to parse scheduler state from {path}: invalid JSON document");
        if (payload.Value is not IReadOnlyDictionary<string, object?> mapping)
        {
            throw new CommandExitException("Scheduler state payload must be a JSON object.");
        }

        foreach (var (name, entry) in mapping)
        {
            if (entry is IReadOnlyDictionary<string, object?> fields)
            {
                normalised[name] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["next_run"] = fields.GetValueOrDefault("next_run"),
                    ["pending"] = fields.GetValueOrDefault("pending"),
                };
            }
        }

        return normalised;
    }

    /// <summary>
    /// <c>_write_schedule_state(scheduler, path)</c>: the snapshot as sorted, indented, ASCII-escaped JSON with a trailing new line, parents
    /// created. A state path Python cannot write raises its <c>OSError</c> text (<see cref="EngineOSError"/>): the parent directory's
    /// (<see cref="EnginePath.MakeDirectories"/>), or the file's, <c>open()</c>'s error for a directory (<see cref="EngineOSError.DirectoryOpenErrno"/>).
    /// </summary>
    public static void WriteScheduleState(ProfileScheduler scheduler, string path)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(path);
        var parent = EngineOsPath.Split(path).Head;
        if (parent.Length > 0)
        {
            EnginePath.MakeDirectories(parent);
        }

        var text = Canonicaliser.DumpsSorted(scheduler.SnapshotState(), indent: true, ensureAscii: true) + "\n";
        if (!string.Equals(Environment.NewLine, "\n", StringComparison.Ordinal))
        {
            text = text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        }

        WriteText(path, text);
    }

    // path.write_text(text, encoding="utf-8"): open() names the path as str(Path) spells it in its OSError.
    private static void WriteText(string path, string text) => EngineTextFile.WriteText(path, text);

    // json.loads(path.read_text(encoding="utf-8")): null when the text is not JSON; a directory raises open()'s error for one (EISDIR on
    // Unix, EACCES on Windows), bytes that are not UTF-8 raise UnicodeDecodeError and the decoder's interpreter limits raise ValueError /
    // RecursionError, all unwrapped (the loaders catch only JSONDecodeError).
    private static JsonValue? ReadJson(string path)
    {
        return EngineJson.TryLoadsOrRaiseLimits(EngineUtf8.Decode(EngineTextFile.ReadBytes(path, path)), out var value) ? new JsonValue(value) : null;
    }

    private sealed record JsonValue(object? Value);
}
