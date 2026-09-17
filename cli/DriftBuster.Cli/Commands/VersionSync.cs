using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster version</c>: propagates the component versions declared in <c>versions.json</c> into the build configuration,
/// manifests, the C# detection engine, docs and tests. A replacement that matches nothing stops the run with <c>No replacements made in {path} for pattern
/// {pattern!r}</c> (exit code 1); the files updated before it stay updated.
/// </summary>
internal static partial class VersionSync
{
    private static readonly string[] ExpectedKeys = ["core", "catalog", "gui", "powershell", "formats"];

    /// <summary><c>load_versions()</c>: <c>versions.json</c> under <paramref name="root"/>, which must hold every expected key.</summary>
    public static IReadOnlyDictionary<string, object?> LoadVersions(string root)
    {
        var data = RunProfileStore.ReadJson(Path.Combine(root, "versions.json"));
        if (data is not IReadOnlyDictionary<string, object?> mapping)
        {
            throw new PythonAttributeException($"'{PythonBuiltins.TypeName(data)}' object has no attribute 'keys'");
        }

        var missing = ExpectedKeys.Where(key => !mapping.ContainsKey(key)).Order(StringComparer.Ordinal).Cast<object?>().ToList();
        return missing.Count > 0
            ? throw new CommandExitException($"versions.json is missing keys: {PythonRepr.Repr(missing)}")
            : mapping;
    }

    /// <summary><c>update_file(path, pattern, replacement, count=count)</c>: <c>re.subn</c> over the file's text, written back in text mode.</summary>
    public static void UpdateFile(string path, string pattern, string replacement, int count = 0)
    {
        var original = TextModeFile.ReadText(path);
        var matches = PythonPattern.Compile(pattern).FindIter(original);
        if (count > 0)
        {
            matches = matches.Take(count);
        }

        var builder = new System.Text.StringBuilder(original.Length);
        var position = 0;
        var applied = 0;
        foreach (var match in matches)
        {
            builder.Append(original, position, match.Start - position).Append(replacement);
            position = match.End;
            applied++;
        }

        if (applied == 0)
        {
            throw new CommandExitException($"No replacements made in {PythonPurePath.Str(path)} for pattern {PythonRepr.StrRepr(pattern)}");
        }

        TextModeFile.WriteText(path, builder.Append(original, position, original.Length - position).ToString());
    }

    /// <summary><c>main()</c>: loads the versions under <paramref name="root"/> and applies every update in order.</summary>
    public static void Run(string root)
    {
        foreach (var update in Updates(root, LoadVersions(root)))
        {
            UpdateFile(update.Path, update.Pattern, update.Replacement, update.Count);
        }
    }

    private static string Str(object? value) => PythonRepr.Str(value);

    private static string At(string root, params string[] parts) => Path.Combine([root, .. parts]);
}
