using System.Text;
using System.Text.RegularExpressions;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Environment-variable and home-directory expansion with the host's rules: <c>$VAR</c>/<c>${VAR}</c> and <c>~</c> on Unix;
/// <c>%VAR%</c>, <c>$VAR</c> and <c>~</c> on Windows.
/// </summary>
public static partial class EngineOsPath
{
    // Unix variable syntax, ASCII names.
    [GeneratedRegex(@"\$(?<name>[A-Za-z0-9_]+|\{[^}]*\}?)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PosixVariable();

    // Windows variable syntax: single-quoted text is left alone, %% and $$ are literal.
    [GeneratedRegex(@"'[^']*'?|%(?<percent>%|[^%]*%?)|\$(?<dollar>\$|[-A-Za-z0-9_]+|\{[^}]*\}?)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WindowsVariable();

    /// <summary>Test seam for reading an environment variable; null means unset.</summary>
    internal static Func<string, string?> GetEnvironmentVariable { get; set; } = Lookup;

    /// <summary>Replaces known variables; unknown ones are left as written.</summary>
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

    /// <summary>Test seam for a named account's home directory; null means no such account.</summary>
    internal static Func<byte[], string?> GetUserHome { get; set; } = UnixPasswd.HomeByName;

    /// <summary>Test seam for the current account's home directory; null means none.</summary>
    internal static Func<string?> GetCurrentUserHome { get; set; } = UnixPasswd.HomeOfCurrentUser;

    /// <summary>
    /// Expands a leading <c>~</c> or <c>~user</c>. Unix: <c>HOME</c>, else the password database; <c>~user</c> from the database,
    /// unchanged when unknown or when the database is unavailable. Windows: <c>USERPROFILE</c> (or <c>HOMEDRIVE</c>+<c>HOMEPATH</c>),
    /// with <c>~user</c> guessed as a sibling profile directory.
    /// </summary>
    /// <exception cref="ArgumentException">A Unix <c>~user</c> name holding NUL.</exception>
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
            // A name with an unpaired surrogate cannot be encoded for the lookup, so it stays unexpanded; NUL is refused.
            if (EngineUtf8.HasUnpairedSurrogate(name))
            {
                return path;
            }

            if (name.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("Null character in path.", nameof(path));
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
