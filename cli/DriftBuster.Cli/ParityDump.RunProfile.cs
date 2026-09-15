using System.Collections;
using System.CommandLine;
using System.Security.Cryptography;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Cli;

/// <summary>The <c>run-profile</c> surface of <c>parity-dump</c> (py_dump.py <c>cmd_run_profile</c>).</summary>
public static partial class ParityDump
{
    private const string WorkdirToken = "<workdir>";

    /// <summary>The fixed run timestamp both dumps hand to <c>execute_profile</c>.</summary>
    internal const string RunProfileTimestamp = "20250102T030405Z";

    private static Command BuildRunProfile()
    {
        var profileArgument = new Argument<string>("profile") { Description = "Run profile JSON file." };
        var workdirArgument = new Argument<string>("workdir") { Description = "Directory copied as the run's working directory." };
        var scratchOption = new Option<string?>("--scratch") { Description = "Directory created for the run and removed after it." };
        var command = new Command("run-profile", "execute_profile result, profile.json, metadata.json and every file under Profiles/.");
        command.Arguments.Add(profileArgument);
        command.Arguments.Add(workdirArgument);
        command.Options.Add(scratchOption);
        command.SetAction(parseResult =>
        {
            WriteLines(parseResult, [RunProfile(parseResult.GetValue(profileArgument)!, parseResult.GetValue(workdirArgument)!, parseResult.GetValue(scratchOption))]);
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Copies <paramref name="workdir"/> to <c>&lt;scratch&gt;/work</c>, makes it the working directory, runs
    /// <see cref="RunProfileExecutor.ExecuteProfile(Backend.Profiles.Run.RunProfile, string?, string?, CancellationToken)"/> over the profile
    /// with <see cref="RunProfileTimestamp"/> and prints <c>result</c> (<c>to_dict</c>), <c>collected</c>, <c>error</c>,
    /// <c>redaction_guard</c> (fix g, only when a guard fired) and <c>profiles</c> (every file under <c>Profiles/</c>), with the working
    /// directory respelled <c>&lt;workdir&gt;</c>. The scratch directory is removed afterwards.
    /// </summary>
    internal static string RunProfile(string profilePath, string workdir, string? scratchPath)
    {
        var profileFull = Path.GetFullPath(profilePath);
        var workdirFull = Path.GetFullPath(workdir);
        var scratch = scratchPath ?? Directory.CreateTempSubdirectory("driftbuster-parity-run-profile-").FullName;
        Directory.CreateDirectory(scratch);
        var work = Path.Combine(scratch, "work");
        var previous = Environment.CurrentDirectory;
        try
        {
            CopyTree(workdirFull, work);
            Environment.CurrentDirectory = work;
            var prefix = Environment.CurrentDirectory;
            if (!PythonJson.TryLoads(PythonUtf8.Decode(File.ReadAllBytes(profileFull)), out var payload))
            {
                throw new InvalidDataException($"The profile file is not valid JSON: {profilePath}");
            }

            var record = RunProfileRecord(payload);
            record["profiles"] = ProfilesListing(work, prefix);
            return Keyed((OrderedDictionary<string, object?>)Respell(record, prefix)!);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static OrderedDictionary<string, object?> RunProfileRecord(object? payload)
    {
        var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        ProfileRunResult? result = null;
        try
        {
            var profile = Backend.Profiles.Run.RunProfile.FromDict(payload);
            result = RunProfileExecutor.ExecuteProfile(profile, timestamp: RunProfileTimestamp);
            record["result"] = result.ToDict();
            record["collected"] = RunCollected(result);
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            record["error"] = ErrorPayload(exc);
        }

        if (result is { RedactionGuards.Count: > 0 })
        {
            record["redaction_guard"] = result.RedactionGuards
                .Select(guard => (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = guard.Path,
                    ["rule"] = guard.Rule,
                    ["line"] = guard.Line,
                })
                .ToList();
        }

        return record;
    }

    /// <summary>
    /// <c>collected</c>: each copied file's source, its source directory under the run when the source has an alias (the alias
    /// directory is compared with the offline runner's), its path under that directory, size and SHA-256, ordered by source, directory
    /// and path (code points).
    /// </summary>
    private static List<object?> RunCollected(ProfileRunResult result)
    {
        var entries = new List<(string Source, string? Directory, string Relative, OrderedDictionary<string, object?> Entry)>();
        foreach (var file in result.Files)
        {
            var parts = PythonPurePath.Parts(PythonPurePath.RelativeTo(file.Destination, result.OutputDir) ?? PathText.Name(file.Destination));
            var aliased = result.Profile.Sources.FirstOrDefault(source => string.Equals(source.Path, file.Source, StringComparison.Ordinal))?.Alias is { Length: > 0 };
            var directory = aliased ? parts[0] : null;
            var relative = parts.Count > 1 ? string.Join('/', parts.Skip(1)) : ".";
            entries.Add((file.Source, directory, relative, new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["source"] = file.Source,
                ["directory"] = directory,
                ["relative"] = relative,
                ["size"] = file.Size,
                ["sha256"] = file.Sha256,
            }));
        }

        return entries
            .Order(Comparer<(string Source, string? Directory, string Relative, OrderedDictionary<string, object?> Entry)>.Create((left, right) =>
            {
                var bySource = PathText.CompareCodePoints(left.Source, right.Source);
                if (bySource != 0)
                {
                    return bySource;
                }

                var byDirectory = PathText.CompareCodePoints(left.Directory ?? string.Empty, right.Directory ?? string.Empty);
                return byDirectory != 0 ? byDirectory : PathText.CompareCodePoints(left.Relative, right.Relative);
            }))
            .Select(entry => (object?)entry.Entry)
            .ToList();
    }

    // Every file under work/Profiles in code point order of its posix path, with the text of profile.json and metadata.json.
    private static List<object?> ProfilesListing(string work, string prefix)
    {
        var root = Path.Combine(work, "Profiles");
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Path: PathText.RelativePosix(work, path), Full: path))
            .OrderBy(entry => entry.Path, Comparer<string>.Create(PathText.CompareCodePoints))
            .Select(entry =>
            {
                var raw = File.ReadAllBytes(entry.Full);
                var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = entry.Path,
                    ["size"] = raw.Length,
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(raw)),
                };
                if (PathText.Name(entry.Full) is "profile.json" or "metadata.json")
                {
                    record["text"] = ReplacingUtf8.GetString(raw).Replace(prefix, WorkdirToken, StringComparison.Ordinal);
                }

                return (object?)record;
            })
            .ToList();
    }

    // py_dump.py _respell: the working directory in every string replaced by <workdir>.
    private static object? Respell(object? value, string prefix) => value switch
    {
        string text => text.Replace(prefix, WorkdirToken, StringComparison.Ordinal),
        IEnumerable<KeyValuePair<string, object?>> pairs => new OrderedDictionary<string, object?>(
            pairs.Select(pair => KeyValuePair.Create(pair.Key, Respell(pair.Value, prefix))), StringComparer.Ordinal),
        IEnumerable items => items.Cast<object?>().Select(item => Respell(item, prefix)).ToList(),
        _ => value,
    };

    // shutil.copytree(source, destination, symlinks=True): directories and files copied, every symlink recreated as a link with the same
    // target text (a dangling link included); an absent source gives an empty directory.
    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        if (!Directory.Exists(source))
        {
            return;
        }

        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var target = Path.Combine(destination, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                if (entry is DirectoryInfo)
                {
                    Directory.CreateSymbolicLink(target, link);
                }
                else
                {
                    File.CreateSymbolicLink(target, link);
                }
            }
            else if (entry is DirectoryInfo)
            {
                CopyTree(entry.FullName, target);
            }
            else
            {
                File.Copy(entry.FullName, target);
            }
        }
    }
}
