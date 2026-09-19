using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>
/// Run profile storage: path expansion, validation, and load/save/list. A profile lives in
/// <c>&lt;base&gt;/Profiles/&lt;safe name&gt;/profile.json</c> (indented, sorted keys, UTF-8, no trailing newline).
/// </summary>
public static class RunProfileStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Expands <c>~</c> and environment variables, then makes the path absolute (lexically).</summary>
    public static string ExpandPath(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EngineOsPath.AbsPath(EngineOsPath.ExpandVars(EngineOsPath.ExpandUser(text)));
    }

    public static bool HasMagic(string pattern) => PathWildcard.HasWildcards(pattern);

    /// <summary>Replaces every code point that is not a letter, digit, "-" or "_" with "-".</summary>
    public static string SafeName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (EngineText.IsAlnum(rune) || rune.Value is '-' or '_')
            {
                builder.Append(rune.ToString());
            }
            else
            {
                builder.Append('-');
            }
        }

        return builder.ToString();
    }

    /// <summary>The leading wildcard-free parts of a pattern (against the working directory when relative), or the working directory.</summary>
    public static string GlobBaseDirectory(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var baseParts = LexicalPath.Parts(pattern).TakeWhile(part => !HasMagic(part)).ToList();
        if (baseParts.Count == 0)
        {
            return Directory.GetCurrentDirectory();
        }

        var joined = LexicalPath.Str(baseParts[0]);
        foreach (var part in baseParts.Skip(1))
        {
            joined = LexicalPath.Join(joined, part);
        }

        return LexicalPath.IsAbsolute(joined) ? joined : LexicalPath.Join(Directory.GetCurrentDirectory(), joined);
    }

    /// <summary>
    /// At least one source, no blank source path, every source (or its glob base) existing, and a baseline that is an existing source.
    /// </summary>
    /// <remarks>
    /// A structured profile (<see cref="RunProfile.IsStructured"/>) is checked as the offline runner collects: a non-optional,
    /// wildcard-free source must exist; an empty glob fails when the run collects it; the baseline need not exist.
    /// </remarks>
    /// <exception cref="ArgumentException">No sources, a blank source path, or a baseline that is not a source path.</exception>
    /// <exception cref="FileNotFoundException">A required source, glob base directory or the baseline is missing.</exception>
    public static void ValidateProfile(RunProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Sources.Count == 0)
        {
            throw new InvalidDataException("At least one source must be provided.");
        }

        var structured = profile.IsStructured;
        foreach (var source in profile.Sources)
        {
            if (EngineText.Strip(source.Path).Length == 0)
            {
                throw new InvalidDataException("Source paths must not be empty.");
            }

            if (!structured)
            {
                RequireExisting(ExpandPath(source.Path), "Path does not exist: ");
            }
            else if (!source.Optional && !HasMagic(source.Path) && !Exists(LexicalPath.Str(RunProfileExecutor.ExpandStructuredPath(source.Path))))
            {
                throw RunProfileExecutor.MissingSource(source.Path);
            }
        }

        if (!string.IsNullOrEmpty(profile.Baseline))
        {
            if (!profile.Sources.Any(source => string.Equals(source.Path, profile.Baseline, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Baseline must be one of the sources.");
            }

            if (!structured)
            {
                RequireExisting(ExpandPath(profile.Baseline), "Baseline path does not exist: ");
            }
        }
    }

    // The glob base directory of a pattern, or the path itself, must exist.
    private static void RequireExisting(string expanded, string missingPrefix)
    {
        if (HasMagic(expanded))
        {
            var baseDirectory = GlobBaseDirectory(expanded);
            if (!Exists(baseDirectory))
            {
                throw new FileNotFoundException($"Glob base directory not found: {baseDirectory}");
            }
        }
        else if (!Exists(expanded))
        {
            throw new FileNotFoundException(missingPrefix + expanded);
        }
    }

    /// <summary><c>&lt;base or cwd&gt;/Profiles</c>, created with its parents.</summary>
    public static string ProfilesRoot(string? baseDir = null)
    {
        var root = LexicalPath.Join(string.IsNullOrEmpty(baseDir) ? Directory.GetCurrentDirectory() : baseDir, "Profiles");
        EnginePath.MakeDirectories(root);
        return root;
    }

    public static string ProfileDirectory(string profileName, string? baseDir = null)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        return JoinName(ProfilesRoot(baseDir), SafeName(profileName));
    }

    /// <exception cref="FileNotFoundException"><c>Profile not found: {name}</c>.</exception>
    public static RunProfile LoadProfile(string profileName, string? baseDir = null)
    {
        var configPath = JoinName(ProfileDirectory(profileName, baseDir), "profile.json");
        if (!Exists(configPath))
        {
            throw new FileNotFoundException($"Profile not found: {profileName}");
        }

        return RunProfile.FromDict(ReadJson(configPath));
    }

    /// <summary>The stored profile, or null when there is no readable <c>profile.json</c>. Creates nothing.</summary>
    internal static RunProfile? TryLoadStoredProfile(string profileName, string? baseDir)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        var root = LexicalPath.Join(string.IsNullOrEmpty(baseDir) ? Directory.GetCurrentDirectory() : baseDir, "Profiles");
        var configPath = JoinName(JoinName(root, SafeName(profileName)), "profile.json");
        try
        {
            return EnginePath.IsFile(configPath) ? RunProfile.FromDict(ReadJson(configPath)) : null;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException
            or InvalidOperationException or InvalidDataException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Validates, writes <c>profile.json</c> and returns the profile directory.</summary>
    public static string SaveProfile(RunProfile profile, string? baseDir = null)
    {
        ValidateProfile(profile);
        var profileDirectory = ProfileDirectory(profile.Name, baseDir);
        EnginePath.MakeDirectories(profileDirectory);
        WriteJson(JoinName(profileDirectory, "profile.json"), profile.ToDict());
        return profileDirectory;
    }

    /// <summary>Every <c>*/profile.json</c> under the root, sorted by path.</summary>
    public static IReadOnlyList<RunProfile> ListProfiles(string? baseDir = null, CancellationToken cancellationToken = default)
    {
        var profiles = new List<RunProfile>();
        foreach (var entry in EnginePath.SortedGlob(ProfilesRoot(baseDir), "*/profile.json", cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            profiles.Add(RunProfile.FromDict(ReadJson(entry)));
        }

        return profiles;
    }

    /// <summary>
    /// Existence after following links. On Linux, lookup errors other than ENOENT, ENOTDIR, EBADF and ELOOP throw; elsewhere the
    /// runtime's checks never throw. A path with an unpaired surrogate or NUL does not exist.
    /// </summary>
    internal static bool Exists(string path)
    {
        if (EngineUtf8.HasUnpairedSurrogate(path))
        {
            return false;
        }

        if (UnixFileType.Stat(path, followSymlinks: true) is { } kind)
        {
            return kind != UnixFileType.Kind.Missing;
        }

        var kernel = EnginePath.KernelPath(path);
        return File.Exists(kernel) || Directory.Exists(kernel);
    }

    /// <summary>Is a directory after following links; throws and handles surrogates as <see cref="Exists"/> does.</summary>
    internal static bool IsDirectory(string path)
    {
        if (EngineUtf8.HasUnpairedSurrogate(path))
        {
            return false;
        }

        if (UnixFileType.Stat(path, followSymlinks: true) is { } kind)
        {
            return kind == UnixFileType.Kind.Directory;
        }

        return Directory.Exists(EnginePath.KernelPath(path));
    }

    internal static string JoinName(string path, string name) => LexicalPath.Join(path, name);

    /// <summary>Writes ASCII-escaped JSON (indent 2, sorted keys) with platform line breaks and no trailing newline.</summary>
    internal static void WriteJson(string path, OrderedDictionary<string, object?> payload)
    {
        var text = Canonicaliser.DumpsSorted(payload, indent: true, ensureAscii: true);
        if (!string.Equals(Environment.NewLine, "\n", StringComparison.Ordinal))
        {
            text = text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        }

        File.WriteAllText(EnginePath.KernelPath(path), text, Utf8);
    }

    /// <summary>Reads a UTF-8 JSON file.</summary>
    /// <exception cref="IOException">The path cannot be opened or read.</exception>
    /// <exception cref="UnauthorizedAccessException">A directory, or access refused.</exception>
    /// <exception cref="InvalidDataException">Not UTF-8, not JSON, or past the decoder's limits.</exception>
    internal static object? ReadJson(string path)
    {
        var text = EngineUtf8.DecodeFile(EngineTextFile.ReadBytes(path, LexicalPath.Str(path)));
        return EngineJson.TryLoadsOrRaiseLimits(text, out var value)
            ? value
            : throw new InvalidDataException($"Invalid JSON document: {path}");
    }
}
