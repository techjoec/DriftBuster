using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>
/// The storage half of <c>run_profiles</c>: path expansion, profile validation, <c>profiles_root</c>, <c>profile_directory</c>,
/// <c>load_profile</c>, <c>save_profile</c> and <c>list_profiles</c>. A profile lives in <c>&lt;base&gt;/Profiles/&lt;safe name&gt;/profile.json</c>,
/// written as <c>json.dumps(profile.to_dict(), indent=2, sort_keys=True)</c> in UTF-8 without a trailing newline.
/// </summary>
public static class RunProfileStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary><c>_expand_path(text)</c>: <c>os.path.abspath(os.path.expandvars(os.path.expanduser(text)))</c>.</summary>
    public static string ExpandPath(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EngineOsPath.AbsPath(EngineOsPath.ExpandVars(EngineOsPath.ExpandUser(text)));
    }

    /// <summary>The text holds a wildcard character, <c>*</c> or <c>?</c> (<see cref="PathWildcard.HasWildcards"/>).</summary>
    public static bool HasMagic(string pattern) => PathWildcard.HasWildcards(pattern);

    /// <summary><c>_safe_name(text)</c>: every code point that is not <c>str.isalnum()</c>, "-" or "_" becomes "-".</summary>
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

    /// <summary>
    /// The leading parts of <see cref="LexicalPath.Parts"/> that hold no <see cref="HasMagic"/> character,
    /// joined, against the working directory when relative; the working directory when there are none.
    /// </summary>
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
    /// <c>_validate_profile(profile)</c>: at least one source, no blank source path, every source's expanded path (or its glob base
    /// directory) existing, and a baseline that is one of the source paths and exists.
    /// </summary>
    /// <remarks>
    /// A structured profile (<see cref="RunProfile.IsStructured"/>) is checked as the offline runner finds its sources instead: a source
    /// that is not optional and holds no glob character must exist once expanded (<c>FileNotFoundError("Path does not exist: {path}")</c>
    /// with the path as written); a glob that matches nothing raises the same error when the run collects it; and the baseline need not
    /// exist (an optional baseline source may be missing).
    /// </remarks>
    /// <exception cref="EngineValueException">No sources, a blank source path, or a baseline that is not a source path.</exception>
    /// <exception cref="FileNotFoundException">A required source path, a glob base directory or the baseline path is missing.</exception>
    public static void ValidateProfile(RunProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Sources.Count == 0)
        {
            throw new EngineValueException("At least one source must be provided.", nameof(profile));
        }

        var structured = profile.IsStructured;
        foreach (var source in profile.Sources)
        {
            if (EngineText.Strip(source.Path).Length == 0)
            {
                throw new EngineValueException("Source paths must not be empty.", nameof(profile));
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
                throw new EngineValueException("Baseline must be one of the sources.", nameof(profile));
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

    /// <summary><c>profiles_root(base_dir)</c>: <c>(base_dir or cwd) / "Profiles"</c>, created with its parents.</summary>
    public static string ProfilesRoot(string? baseDir = null)
    {
        var root = LexicalPath.Join(string.IsNullOrEmpty(baseDir) ? Directory.GetCurrentDirectory() : baseDir, "Profiles");
        EnginePath.MakeDirectories(root);
        return root;
    }

    /// <summary><c>profile_directory(profile_name, base_dir)</c>: <see cref="ProfilesRoot"/> joined with <see cref="SafeName"/>.</summary>
    public static string ProfileDirectory(string profileName, string? baseDir = null)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        return JoinName(ProfilesRoot(baseDir), SafeName(profileName));
    }

    /// <summary><c>load_profile(profile_name, base_dir=...)</c>.</summary>
    /// <exception cref="FileNotFoundException"><c>Profile not found: {profile_name}</c>.</exception>
    public static RunProfile LoadProfile(string profileName, string? baseDir = null)
    {
        var configPath = JoinName(ProfileDirectory(profileName, baseDir), "profile.json");
        if (!Exists(configPath))
        {
            throw new FileNotFoundException($"Profile not found: {profileName}");
        }

        return RunProfile.FromDict(ReadJson(configPath));
    }

    /// <summary>
    /// The profile stored under <paramref name="profileName"/> as <see cref="LoadProfile"/> reads it, or null when there is no
    /// <c>profile.json</c> or it cannot be read as a profile. Nothing is created.
    /// </summary>
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
            or InvalidOperationException or EngineAttributeException or EngineUnicodeDecodeException)
        {
            return null;
        }
    }

    /// <summary><c>save_profile(profile, base_dir=...)</c>: validates, writes <c>profile.json</c> and returns the profile directory.</summary>
    public static string SaveProfile(RunProfile profile, string? baseDir = null)
    {
        ValidateProfile(profile);
        var profileDirectory = ProfileDirectory(profile.Name, baseDir);
        EnginePath.MakeDirectories(profileDirectory);
        WriteJson(JoinName(profileDirectory, "profile.json"), profile.ToDict());
        return profileDirectory;
    }

    /// <summary><c>list_profiles(base_dir=...)</c>: every <c>*/profile.json</c> under the root, in <c>sorted()</c> path order.</summary>
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
    /// <c>Path.exists()</c>: a <c>stat</c> that follows links succeeds; on Linux a lookup error other than <c>ENOENT</c>, <c>ENOTDIR</c>,
    /// <c>EBADF</c> or <c>ELOOP</c> raises. Elsewhere the runtime's existence checks answer and never raise, including for an access-denied
    /// lookup on Windows. A path holding an unpaired surrogate or a NUL does not exist: the runtime would look the former up under its U+FFFD
    /// spelling, a different entry.
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

    /// <summary>
    /// <c>Path.is_dir()</c>: a <c>stat</c> that follows links reports a directory; raises as <see cref="Exists"/> raises, and is false
    /// for a path holding an unpaired surrogate as <see cref="Exists"/> is.
    /// </summary>
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

    /// <summary><c>path / name</c> for a single name.</summary>
    internal static string JoinName(string path, string name) => LexicalPath.Join(path, name);

    /// <summary>
    /// <c>path.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8")</c>: ASCII-escaped JSON, each line
    /// break written as the platform's (text mode), no trailing newline.
    /// </summary>
    internal static void WriteJson(string path, OrderedDictionary<string, object?> payload)
    {
        var text = Canonicaliser.DumpsSorted(payload, indent: true, ensureAscii: true);
        if (!string.Equals(Environment.NewLine, "\n", StringComparison.Ordinal))
        {
            text = text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        }

        File.WriteAllText(EnginePath.KernelPath(path), text, Utf8);
    }

    /// <summary><c>json.loads(path.read_text(encoding="utf-8"))</c>.</summary>
    /// <exception cref="IOException">The path is a directory (<c>open()</c>'s error for one, <see cref="OsError.DirectoryOpenErrno"/>), or
    /// cannot be opened or read (Python's <c>OSError</c> text, naming the path as <c>str(Path)</c> spells it when <c>open()</c> raised).</exception>
    /// <exception cref="EngineUnicodeDecodeException">The bytes are not UTF-8 (<c>UnicodeDecodeError</c>).</exception>
    /// <exception cref="EngineValueException">The text is not a JSON document (<c>JSONDecodeError</c>), or holds an integer past the decoder's
    /// digit limit (<c>ValueError</c>, Python's text).</exception>
    /// <exception cref="EngineRecursionException">Containers nested past the decoder's limit (<c>RecursionError</c>).</exception>
    internal static object? ReadJson(string path)
    {
        var text = EngineUtf8.Decode(EngineTextFile.ReadBytes(path, LexicalPath.Str(path)));
        return EngineJson.TryLoadsOrRaiseLimits(text, out var value)
            ? value
            : throw new EngineValueException($"Invalid JSON document: {path}", nameof(path));
    }
}
