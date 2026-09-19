using System.Text.Json;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Scheduling;

/// <summary>
/// The schedule files under the profiles root: <c>schedules.json</c> (<see cref="ScheduleManifest"/>) and <c>scheduler-state.json</c>
/// (per schedule <see cref="ScheduleStateEntry"/>). Both are read strictly (<see cref="ModelJson"/>); a file that cannot be read
/// raises <see cref="ScheduleException"/> naming the file and the JSON path. A missing file is empty.
/// </summary>
public static class ScheduleStore
{
    /// <summary>The override as given, else <c>schedules.json</c> under the profiles root.</summary>
    public static string DefaultConfigPath(string? baseDir, string? overridePath)
        => overridePath ?? Path.Join(RunProfileStore.ProfilesRoot(baseDir), "schedules.json");

    /// <summary>The override as given, else <c>scheduler-state.json</c> under the profiles root.</summary>
    public static string DefaultStatePath(string? baseDir, string? overridePath)
        => overridePath ?? Path.Join(RunProfileStore.ProfilesRoot(baseDir), "scheduler-state.json");

    public static ScheduleManifest LoadManifest(string path)
        => Read(path, ModelJson.TypeInfo<ScheduleManifest>()) ?? new ScheduleManifest([]);

    public static IReadOnlyDictionary<string, ScheduleStateEntry> LoadState(string path)
        => Read(path, ModelJson.TypeInfo<IReadOnlyDictionary<string, ScheduleStateEntry>>()) ?? new Dictionary<string, ScheduleStateEntry>(StringComparer.Ordinal);

    public static void SaveState(ProfileScheduler scheduler, string path)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        Write(path, ModelJson.Serialize(scheduler.SnapshotState()));
    }

    /// <summary>The GUI's view of the manifest under <paramref name="baseDir"/>.</summary>
    public static ScheduleListResult ListSchedules(string? baseDir)
        => new(LoadManifest(DefaultConfigPath(baseDir, overridePath: null)).Schedules);

    /// <summary>Validates every schedule (<see cref="Validate"/>) and writes the manifest in the given order.</summary>
    public static void SaveSchedules(IEnumerable<ScheduleDefinition> schedules, string? baseDir)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        var manifest = new ScheduleManifest([.. schedules]);
        Validate(manifest.Schedules);
        Write(DefaultConfigPath(baseDir, overridePath: null), ModelJson.Serialize(manifest));
    }

    /// <summary>The specs of every schedule, in order; <see cref="ScheduleException"/> names the first invalid one by index and field.</summary>
    public static IReadOnlyList<ScheduleSpec> Validate(IReadOnlyList<ScheduleDefinition> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        var specs = new List<ScheduleSpec>(schedules.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < schedules.Count; index++)
        {
            ScheduleSpec spec;
            try
            {
                spec = ScheduleSpec.From(schedules[index]);
            }
            catch (ScheduleException exc)
            {
                throw new ScheduleException($"schedules[{index}].{exc.Message}", exc);
            }

            if (!names.Add(spec.Name))
            {
                throw new ScheduleException($"schedules[{index}].name: '{spec.Name}' is used by an earlier schedule.");
            }

            specs.Add(spec);
        }

        return specs;
    }

    /// <summary>The refusal <see cref="ScheduleSpec.From"/> gives one schedule, or null when it is valid.</summary>
    public static string? ValidationError(ScheduleDefinition schedule)
    {
        try
        {
            _ = ScheduleSpec.From(schedule);
            return null;
        }
        catch (ScheduleException exc)
        {
            return exc.Message;
        }
    }

    private static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo) ?? throw new ScheduleException($"{path}: the file holds null.");
        }
        catch (JsonException exc)
        {
            throw new ScheduleException($"{path}: {exc.Path ?? "$"}: {exc.Message}", exc);
        }
    }

    private static void Write(string path, string text)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(path, text);
    }
}
