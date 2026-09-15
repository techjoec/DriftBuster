using System.CommandLine;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Scheduling;

namespace DriftBuster.Cli;

/// <summary>
/// The <c>profile-store</c> and <c>profile-diff</c> surfaces of <c>parity-dump</c> (py_dump.py <c>cmd_profile_store</c>,
/// <c>cmd_profile_diff</c>), and the Python exception names every phase 6 surface prints.
/// </summary>
public static partial class ParityDump
{
    private const string MissingConfigIdentifier = "<no-such-config>";

    private static Command BuildProfileStore()
    {
        var payloadArgument = new Argument<string>("payload") { Description = "ProfileStore payload JSON file." };
        var tagsOption = new Option<string?>("--tags") { Description = "Comma-separated activation tags." };
        var pathOption = new Option<string?>("--path") { Description = "Relative path for matching_configs." };
        var command = new Command("profile-store", "Summary, tag helpers, lookups and to_dict round trip of a profile store.");
        command.Arguments.Add(payloadArgument);
        command.Options.Add(tagsOption);
        command.Options.Add(pathOption);
        command.SetAction(parseResult =>
        {
            WriteLines(parseResult, [ProfileStore(parseResult.GetValue(payloadArgument)!, parseResult.GetValue(tagsOption), parseResult.GetValue(pathOption))]);
            return 0;
        });
        return command;
    }

    private static Command BuildProfileDiff()
    {
        var baselineArgument = new Argument<string>("baseline") { Description = "Baseline summary JSON file." };
        var currentArgument = new Argument<string>("current") { Description = "Current summary JSON file." };
        var command = new Command("profile-diff", "diff_summary_snapshots of two summary payloads.");
        command.Arguments.Add(baselineArgument);
        command.Arguments.Add(currentArgument);
        command.SetAction(parseResult =>
        {
            WriteLines(parseResult, [ProfileDiff(parseResult.GetValue(baselineArgument)!, parseResult.GetValue(currentArgument)!)]);
            return 0;
        });
        return command;
    }

    /// <summary>
    /// <c>type(exc).__name__</c> of the Python exception each port exception stands for (see the exception mapping of
    /// <see cref="PythonBuiltins"/>, <see cref="DetectionProfileStore"/> and <see cref="ScheduleException"/>); any other exception keeps
    /// its runtime name, which never equals a Python name and so fails the compare.
    /// </summary>
    internal static string PythonErrorName(Exception exc) => exc switch
    {
        CommandExitException => "SystemExit",
        ScheduleException => "ScheduleError",
        PythonValueException => "ValueError",
        PythonRecursionException => "RecursionError",
        PythonTypeException => "TypeError",
        PythonIndexException => "IndexError",
        KeyNotFoundException => "KeyError",
        FileNotFoundException => "FileNotFoundError",
        OverflowException => "OverflowError",
        PythonAttributeException => "AttributeError",
        PythonUnicodeDecodeException => "UnicodeDecodeError",
        IOException { HResult: > 0 and < 4096 } => PythonOSError.TypeName(exc.HResult),
        UnauthorizedAccessException => "PermissionError",
        _ => exc.GetType().Name,
    };

    internal static OrderedDictionary<string, object?> ErrorPayload(Exception exc) => new(StringComparer.Ordinal)
    {
        ["type"] = PythonErrorName(exc),
        ["message"] = exc.Message,
    };

    private static string Keyed(OrderedDictionary<string, object?> record)
    {
        record["key_order"] = CanonicalJson.KeyOrder(record);
        return CanonicalJson.Serialize(record);
    }

    // py_dump.py _stage: record[key] = produce(), or {"error": ...} when it raises.
    private static void Stage(OrderedDictionary<string, object?> record, string key, Func<object?> produce)
    {
        try
        {
            record[key] = produce();
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            record[key] = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["error"] = ErrorPayload(exc) };
        }
    }

    private static OrderedDictionary<string, object?> AppliedPayload(AppliedProfileConfig match) => new(StringComparer.Ordinal)
    {
        ["profile"] = match.Profile.Name,
        ["config"] = match.Config.Identifier,
        ["path"] = match.Config.Path,
        ["path_glob"] = match.Config.PathGlob,
        ["expected_format"] = match.Config.ExpectedFormat,
        ["expected_variant"] = match.Config.ExpectedVariant,
    };

    /// <summary>
    /// <c>_store_from_payload(_load_json(payload))</c>, then <c>summary</c>, <c>applicable_profiles</c> and <c>matching_configs</c> for the
    /// tags (split on commas) and path, <c>find_config</c> for every identifier plus one that is not registered, <c>to_dict</c> and the
    /// <c>to_dict</c> of a store rebuilt from it; a failing build is the whole record's <c>error</c>, a failing stage that stage's.
    /// </summary>
    internal static string ProfileStore(string payloadPath, string? tags, string? relativePath)
    {
        var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        DetectionProfileStore store;
        try
        {
            store = DetectionProfileCommands.StoreFromPayload(DetectionProfileCommands.LoadJson(payloadPath));
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            record["error"] = ErrorPayload(exc);
            return Keyed(record);
        }

        var tagList = tags?.Split(',');
        Stage(record, "summary", store.Summary);
        Stage(record, "applicable_profiles", () => store.ApplicableProfiles(tagList).Select(profile => (object?)profile.Name).ToList());
        Stage(record, "matching_configs", () => store.MatchingConfigs(tagList, relativePath).Select(match => (object?)AppliedPayload(match)).ToList());
        var identifiers = store.Profiles().SelectMany(profile => profile.Configs).Select(config => config.Identifier).Append(MissingConfigIdentifier);
        record["find_config"] = identifiers
            .Select(identifier => (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = identifier,
                ["matches"] = store.FindConfig(identifier).Select(match => (object?)AppliedPayload(match)).ToList(),
            })
            .ToList();
        var snapshot = store.ToDict();
        record["to_dict"] = snapshot;
        Stage(record, "round_trip", () => DetectionProfileCommands.StoreFromPayload(snapshot).ToDict());
        return Keyed(record);
    }

    /// <summary><c>diff_summary_snapshots(_load_json(baseline), _load_json(current))</c> as <c>diff</c>, or the <c>error</c>.</summary>
    internal static string ProfileDiff(string baselinePath, string currentPath)
    {
        var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            record["diff"] = DetectionProfileCommands.Diff(baselinePath, currentPath);
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            record["error"] = ErrorPayload(exc);
        }

        return Keyed(record);
    }
}
