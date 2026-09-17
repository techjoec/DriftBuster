namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// CPython 3.13 <c>ntpath.realpath(path)</c> (<c>strict=False</c>), which <c>Path.resolve()</c> runs on Windows: the path is normalised,
/// <c>nul</c> becomes <c>\\.\NUL</c>, a relative path is joined onto the working directory, and <c>_getfinalpathname</c> names the entry
/// the OS reaches (the stored letter case, 8.3 short names expanded, a mapped or subst drive replaced by what it maps, links followed).
/// When the OS cannot name it, <c>_getfinalpathname_nonstrict</c> names the longest prefix it can (following links itself where a
/// link's target is missing, reading a name's stored spelling without opening it where access is refused) and joins the rest as
/// written. The <c>\\?\</c> prefix the OS answers with is dropped when the input had none and the shorter spelling names the same path
/// (or fails the same way the input failed).
/// </summary>
internal static class PythonNtRealPath
{
    private const string Prefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";
    private const string NewUncPrefix = @"\\";

    // _getfinalpathname_nonstrict's allowed_winerror.
    private static readonly int[] StopResolving = [1, 2, 3, 5, 21, 32, 50, 53, 65, 67, 87, 123, 161, 1005, 1920, 1921];

    // _readlink_deep's allowed_winerror.
    private static readonly int[] StopReadingLinks = [1, 2, 3, 5, 21, 32, 50, 67, 87, 4390, 4392, 4393];

    // The errors after which the entry's stored name is read with _findfirstfile instead of split off as written.
    private static readonly int[] FindName = [1, 5, 32, 50, 87, 1920, 1921];

    /// <summary><c>ntpath.realpath(path)</c> over <paramref name="system"/>.</summary>
    public static string RealPath(string path, INtPathSystem system)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(system);
        path = PythonNtPath.NormPath(path);
        if (string.Equals(NormCase(path), "nul", StringComparison.Ordinal))
        {
            return @"\\.\NUL";
        }

        var hadPrefix = path.StartsWith(Prefix, StringComparison.Ordinal);
        if (!hadPrefix && !PythonNtPath.IsAbs(path))
        {
            path = PythonNtPath.Join(system.CurrentDirectory, path);
        }

        var initialWinError = 0;
        try
        {
            path = system.GetFinalPathName(path);
        }
        catch (PythonValueException)
        {
            // gh-106242: an embedded NUL; the path is kept as made absolute.
            path = PythonNtPath.NormPath(path);
        }
        catch (NtPathException exc)
        {
            initialWinError = exc.WinError;
            path = GetFinalPathNameNonStrict(path, system);
        }

        if (!hadPrefix && path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            path = StripPrefix(path, initialWinError, system);
        }

        return path;
    }

    // The \\?\ (or \\?\UNC\) prefix removed when the shorter spelling resolves to the same path, or fails as the input failed.
    private static string StripPrefix(string path, int initialWinError, INtPathSystem system)
    {
        var shorter = path.StartsWith(UncPrefix, StringComparison.Ordinal) ? NewUncPrefix + path[UncPrefix.Length..] : path[Prefix.Length..];
        try
        {
            return string.Equals(system.GetFinalPathName(shorter), path, StringComparison.Ordinal) ? shorter : path;
        }
        catch (PythonValueException)
        {
            return path;
        }
        catch (NtPathException exc)
        {
            return exc.WinError == initialWinError ? shorter : path;
        }
    }

    // _getfinalpathname_nonstrict(path): as much of the path as the OS names, the rest joined as written.
    private static string GetFinalPathNameNonStrict(string path, INtPathSystem system)
    {
        var tail = string.Empty;
        while (path.Length > 0)
        {
            NtPathException failure;
            try
            {
                path = system.GetFinalPathName(path);
                return tail.Length > 0 ? PythonNtPath.Join(path, tail) : path;
            }
            catch (NtPathException exc)
            {
                if (!StopResolving.Contains(exc.WinError))
                {
                    throw;
                }

                failure = exc;
            }

            try
            {
                var newPath = ReadLinkDeep(path, system);
                if (!string.Equals(newPath, path, StringComparison.Ordinal))
                {
                    return tail.Length > 0 ? PythonNtPath.Join(newPath, tail) : newPath;
                }
            }
            catch (NtPathException)
            {
                // A link that cannot be read: keep traversing.
            }

            string name;
            if (FindName.Contains(failure.WinError))
            {
                try
                {
                    name = system.FindFirstFile(path);
                    path = PythonNtPath.Split(path).Head;
                }
                catch (NtPathException)
                {
                    (path, name) = PythonNtPath.Split(path);
                }
            }
            else
            {
                (path, name) = PythonNtPath.Split(path);
            }

            if (path.Length > 0 && name.Length == 0)
            {
                return path + tail;
            }

            tail = tail.Length > 0 ? PythonNtPath.Join(name, tail) : name;
        }

        return tail;
    }

    // _readlink_deep(path): the link chain followed as far as it can be read, a relative target resolved against the link's directory.
    private static string ReadLinkDeep(string path, INtPathSystem system)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(NormCase(path)))
        {
            var oldPath = path;
            try
            {
                path = system.ReadLink(path);
                if (!PythonNtPath.IsAbs(path))
                {
                    if (!system.IsLink(oldPath))
                    {
                        // Something other than a symlink: it is not known what it resolves against.
                        return oldPath;
                    }

                    path = PythonNtPath.NormPath(PythonNtPath.Join(PythonNtPath.Split(oldPath).Head, path));
                }
            }
            catch (NtPathException exc)
            {
                if (StopReadingLinks.Contains(exc.WinError))
                {
                    return oldPath;
                }

                throw;
            }
            catch (PythonValueException)
            {
                // A reparse point that is not a link.
                return oldPath;
            }
        }

        return path;
    }

    // ntpath.normcase: separators made backslashes, lower-cased.
    private static string NormCase(string path) => PythonText.Lower(path.Replace('/', '\\'));
}
