using System.Text;
using System.Text.RegularExpressions;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// <c>os.path.expandvars</c> and <c>os.path.expanduser</c> for the host platform: <c>posixpath</c> everywhere but Windows,
/// <c>ntpath</c> there (CPython 3.13).
/// </summary>
public static partial class EngineOsPath
{
    // posixpath._varpattern compiled with re.ASCII.
    [GeneratedRegex(@"\$(?<name>[A-Za-z0-9_]+|\{[^}]*\}?)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PosixVariable();

    // ntpath._varpattern compiled with re.ASCII.
    [GeneratedRegex(@"'[^']*'?|%(?<percent>%|[^%]*%?)|\$(?<dollar>\$|[-A-Za-z0-9_]+|\{[^}]*\}?)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WindowsVariable();

    /// <summary>Test seam for <c>os.environ[name]</c>: null stands for the <c>KeyError</c> of an unset variable.</summary>
    internal static Func<string, string?> GetEnvironmentVariable { get; set; } = Lookup;

    /// <summary>
    /// <c>os.path.expandvars(path)</c>: <c>$name</c> and <c>${name}</c> (and on Windows <c>%name%</c>, <c>$$</c>, <c>%%</c> and
    /// single-quoted text left alone) replaced by the variable's value; unknown variables are left unchanged.
    /// </summary>
    public static string ExpandVars(string path) => ExpandVars(path, OperatingSystem.IsWindows());

    internal static string ExpandVars(string path, bool windows)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!windows)
        {
            return !path.Contains('$', StringComparison.Ordinal) ? path : PosixVariable().Replace(path, ReplacePosix);
        }

        return !path.Contains('$', StringComparison.Ordinal) && !path.Contains('%', StringComparison.Ordinal)
            ? path
            : WindowsVariable().Replace(path, ReplaceWindows);
    }

    private static string ReplacePosix(Match match)
    {
        var name = match.Groups["name"].Value;
        if (name.StartsWith('{'))
        {
            if (!name.EndsWith('}') || name.Length < 2)
            {
                return match.Value;
            }

            name = name[1..^1];
        }

        return GetEnvironmentVariable(name) ?? match.Value;
    }

    private static string ReplaceWindows(Match match)
    {
        string name;
        if (match.Groups["dollar"].Success)
        {
            name = match.Groups["dollar"].Value;
            if (string.Equals(name, "$", StringComparison.Ordinal))
            {
                return name;
            }

            if (name.StartsWith('{'))
            {
                if (!name.EndsWith('}') || name.Length < 2)
                {
                    return match.Value;
                }

                name = name[1..^1];
            }
        }
        else if (match.Groups["percent"].Success)
        {
            name = match.Groups["percent"].Value;
            if (string.Equals(name, "%", StringComparison.Ordinal))
            {
                return name;
            }

            if (!name.EndsWith('%'))
            {
                return match.Value;
            }

            name = name[..^1];
        }
        else
        {
            return match.Value;
        }

        return GetEnvironmentVariable(name) ?? match.Value;
    }

    /// <summary>Test seam for <c>pwd.getpwnam(name).pw_dir</c> over the encoded name: null stands for <c>KeyError</c>.</summary>
    internal static Func<byte[], string?> GetUserHome { get; set; } = UnixPasswd.HomeByName;

    /// <summary>Test seam for <c>pwd.getpwuid(os.getuid()).pw_dir</c>: null stands for <c>KeyError</c>.</summary>
    internal static Func<string?> GetCurrentUserHome { get; set; } = UnixPasswd.HomeOfCurrentUser;

    /// <summary>
    /// <c>os.path.expanduser(path)</c>: a leading <c>~</c> becomes a home directory. On posix <c>~</c> uses <c>HOME</c> (the
    /// password database's entry for the current user when unset) and <c>~user</c> the named account's entry, with trailing
    /// <c>/</c> stripped from the home; an account that does not exist leaves the path unchanged. Where the password database
    /// cannot be read (<see cref="UnixPasswd.Available"/>), <c>~</c> falls back to the runtime's profile directory and
    /// <c>~user</c> is left unchanged. On Windows <c>USERPROFILE</c> (or <c>HOMEDRIVE</c> + <c>HOMEPATH</c>) is used and
    /// <c>~user</c> is guessed as a sibling profile directory.
    /// </summary>
    /// <exception cref="EngineValueException">A posix <c>~user</c> name holding a NUL character (<c>embedded null byte</c>).</exception>
    public static string ExpandUser(string path) => ExpandUser(path, OperatingSystem.IsWindows());

    internal static string ExpandUser(string path, bool windows)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.StartsWith('~'))
        {
            return path;
        }

        return windows ? ExpandUserWindows(path) : ExpandUserPosix(path);
    }

    private static string ExpandUserPosix(string path)
    {
        var end = path.IndexOf('/', 1);
        if (end < 0)
        {
            end = path.Length;
        }

        string? home;
        if (end == 1)
        {
            home = GetEnvironmentVariable("HOME") ?? GetCurrentUserHome();
            if (home is null && !UnixPasswd.Available)
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (home.Length == 0)
                {
                    return path;
                }
            }
        }
        else
        {
            var name = path[1..end];
            // pwd.getpwnam encodes the name with the filesystem encoding: an unpaired surrogate raises UnicodeEncodeError, which
            // is not reproduced (the root keeps its surrogate and is never looked up), and a NUL raises ValueError.
            if (EngineUtf8.HasUnpairedSurrogate(name))
            {
                return path;
            }

            if (name.Contains('\0', StringComparison.Ordinal))
            {
                throw new EngineValueException("embedded null byte", nameof(path));
            }

            home = GetUserHome(Encoding.UTF8.GetBytes(name));
        }

        if (home is null)
        {
            return path;
        }

        var expanded = home.TrimEnd('/') + path[end..];
        return expanded.Length == 0 ? "/" : expanded;
    }

    private static string ExpandUserWindows(string path)
    {
        var end = 1;
        while (end < path.Length && path[end] is not ('\\' or '/'))
        {
            end++;
        }

        string home;
        if (GetEnvironmentVariable("USERPROFILE") is { } profile)
        {
            home = profile;
        }
        else if (GetEnvironmentVariable("HOMEPATH") is { } homePath)
        {
            home = Path.Join(GetEnvironmentVariable("HOMEDRIVE") ?? string.Empty, homePath);
        }
        else
        {
            return path;
        }

        if (end != 1)
        {
            var targetUser = path[1..end];
            var currentUser = GetEnvironmentVariable("USERNAME");
            if (!string.Equals(targetUser, currentUser, StringComparison.Ordinal))
            {
                if (!string.Equals(currentUser, Path.GetFileName(home), StringComparison.Ordinal))
                {
                    return path;
                }

                home = Path.Join(Path.GetDirectoryName(home), targetUser);
            }
        }

        return home + path[end..];
    }

    private static string? Lookup(string name)
    {
        if (name.Length == 0 || name.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Environment.GetEnvironmentVariable(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
