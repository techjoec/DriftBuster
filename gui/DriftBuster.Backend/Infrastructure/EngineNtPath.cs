namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// CPython 3.13 <c>ntpath</c> string operations (<c>splitroot</c>, <c>splitdrive</c>, <c>isabs</c>, <c>join</c>, <c>split</c>), which
/// <c>PureWindowsPath</c> and <c>os.path</c> run on Windows. They touch no file system, so they behave the same on every host and
/// are compared with CPython's <c>ntpath</c> on Linux.
/// </summary>
/// <remarks>
/// A UNC drive is <c>\\server\share</c> (or <c>\\?\UNC\server\share</c>) without the separator after the share: that separator is
/// the root. <see cref="Path.GetPathRoot(string)"/> gives the same drive but no root, so it must not stand in for these.
/// Indexes such as <c>p[1:2]</c> are code point positions in Python, so a leading surrogate pair counts as one character.
/// </remarks>
public static class EngineNtPath
{
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary><c>ntpath.splitroot(p)</c>: the drive, the root (one separator or empty) and the rest.</summary>
    public static (string Drive, string Root, string Remainder) SplitRoot(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normp = path.Replace('/', '\\');
        if (normp.StartsWith('\\'))
        {
            if (normp.Length < 2 || normp[1] != '\\')
            {
                return (string.Empty, path[..1], path[1..]);
            }

            var start = normp.Length >= UncPrefix.Length && IsUncPrefix(normp) ? UncPrefix.Length : 2;
            var index = normp.IndexOf('\\', start);
            if (index == -1)
            {
                return (path, string.Empty, string.Empty);
            }

            var index2 = normp.IndexOf('\\', index + 1);
            return index2 == -1 ? (path, string.Empty, string.Empty) : (path[..index2], path.Substring(index2, 1), path[(index2 + 1)..]);
        }

        var second = normp.Length > 1 && char.IsHighSurrogate(normp[0]) && char.IsLowSurrogate(normp[1]) ? 2 : 1;
        if (normp.Length > second && normp[second] == ':')
        {
            var driveLength = second + 1;
            return normp.Length > driveLength && normp[driveLength] == '\\'
                ? (path[..driveLength], path.Substring(driveLength, 1), path[(driveLength + 1)..])
                : (path[..driveLength], string.Empty, path[driveLength..]);
        }

        return (string.Empty, string.Empty, path);
    }

    // normp[:8].upper() == '\\?\UNC\': only ASCII u, n and c upper-case to U, N and C.
    private static bool IsUncPrefix(string normp)
        => normp[0] == '\\' && normp[1] == '\\' && normp[2] == '?' && normp[3] == '\\' && (normp[4] is 'U' or 'u')
            && (normp[5] is 'N' or 'n') && (normp[6] is 'C' or 'c') && normp[7] == '\\';

    /// <summary><c>ntpath.splitdrive(p)</c>: the drive and everything after it.</summary>
    public static (string Drive, string Remainder) SplitDrive(string path)
    {
        var (drive, root, rest) = SplitRoot(path);
        return (drive, root + rest);
    }

    /// <summary><c>ntpath.isabs(s)</c>: a UNC or device path, or a drive followed by a root.</summary>
    public static bool IsAbs(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var head = CodePointPrefix(path, 3).Replace('/', '\\');
        return (head.Length > 0 && head.AsSpan(PrefixLength(head, 1)).StartsWith(@":\", StringComparison.Ordinal))
            || head.StartsWith(@"\\", StringComparison.Ordinal);
    }

    // The first count code points of text.
    private static string CodePointPrefix(string text, int count) => text[..PrefixLength(text, count)];

    private static int PrefixLength(string text, int count)
    {
        var index = 0;
        for (var seen = 0; seen < count && index < text.Length; seen++)
        {
            index += index + 1 < text.Length && char.IsHighSurrogate(text[index]) && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
        }

        return index;
    }

    /// <summary><c>ntpath.join(path, *paths)</c>.</summary>
    public static string Join(string path, params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var (resultDrive, resultRoot, resultPath) = SplitRoot(path);
        foreach (var next in paths)
        {
            var (drive, root, rest) = SplitRoot(next);
            if (root.Length > 0)
            {
                if (drive.Length > 0 || resultDrive.Length == 0)
                {
                    resultDrive = drive;
                }

                resultRoot = root;
                resultPath = rest;
                continue;
            }

            if (drive.Length > 0 && !string.Equals(drive, resultDrive, StringComparison.Ordinal))
            {
                if (!string.Equals(EngineText.Lower(drive), EngineText.Lower(resultDrive), StringComparison.Ordinal))
                {
                    resultDrive = drive;
                    resultRoot = root;
                    resultPath = rest;
                    continue;
                }

                resultDrive = drive;
            }

            if (resultPath.Length > 0 && resultPath[^1] is not ('\\' or '/'))
            {
                resultPath += "\\";
            }

            resultPath += rest;
        }

        return resultPath.Length > 0 && resultRoot.Length == 0 && resultDrive.Length > 0 && resultDrive[^1] is not (':' or '\\' or '/')
            ? resultDrive + "\\" + resultPath
            : resultDrive + resultRoot + resultPath;
    }

    /// <summary>
    /// <c>ntpath.normpath(path)</c>: separators made backslashes, empty and <c>.</c> names dropped, <c>..</c> removing the name before
    /// it (dropped right after a root, kept at the start of a relative path), and <c>.</c> for a result with no anchor and no names.
    /// </summary>
    public static string NormPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var (drive, root, rest) = SplitRoot(path.Replace('/', '\\'));
        var components = rest.Split('\\').ToList();
        var index = 0;
        while (index < components.Count)
        {
            var component = components[index];
            if (component.Length == 0 || string.Equals(component, ".", StringComparison.Ordinal))
            {
                components.RemoveAt(index);
            }
            else if (!string.Equals(component, "..", StringComparison.Ordinal))
            {
                index++;
            }
            else if (index > 0 && !string.Equals(components[index - 1], "..", StringComparison.Ordinal))
            {
                components.RemoveRange(index - 1, 2);
                index--;
            }
            else if (index == 0 && root.Length > 0)
            {
                components.RemoveAt(index);
            }
            else
            {
                index++;
            }
        }

        var prefix = drive + root;
        return prefix.Length == 0 && components.Count == 0 ? "." : prefix + string.Join('\\', components);
    }

    /// <summary><c>ntpath.split(p)</c>: the drive, root and directory with trailing separators removed, and the last name.</summary>
    public static (string Head, string Tail) Split(string path)
    {
        var (drive, root, rest) = SplitRoot(path);
        var index = rest.Length;
        while (index > 0 && rest[index - 1] is not ('\\' or '/'))
        {
            index--;
        }

        return (drive + root + rest[..index].TrimEnd('\\', '/'), rest[index..]);
    }
}
