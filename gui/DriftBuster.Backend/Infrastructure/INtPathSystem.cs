namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// The Windows calls <c>ntpath.realpath</c> makes (<see cref="EngineNtRealPath"/>), so the algorithm runs over a fake on every host.
/// Each failing call raises <see cref="NtPathException"/> with the Windows error code Python reads as <c>winerror</c>.
/// </summary>
internal interface INtPathSystem
{
    /// <summary><c>os.getcwd()</c>.</summary>
    string CurrentDirectory { get; }

    /// <summary>
    /// <c>nt._getfinalpathname(path)</c>: the path opened (a directory or a file, links followed) and named by
    /// <c>GetFinalPathNameByHandleW(VOLUME_NAME_DOS)</c>, which starts with <c>\\?\</c> (or <c>\\?\UNC\</c>).
    /// </summary>
    /// <exception cref="NtPathException">The path could not be opened or named.</exception>
    /// <exception cref="EngineValueException">The path holds a NUL character (<c>embedded null character</c>).</exception>
    string GetFinalPathName(string path);

    /// <summary><c>nt._nt_readlink(path)</c>: the target of a symbolic link or junction as its reparse data spells it.</summary>
    /// <exception cref="NtPathException">The path is not a reparse point, or cannot be read.</exception>
    /// <exception cref="EngineValueException">The reparse point is not a link (<c>not a symbolic link</c>).</exception>
    string ReadLink(string path);

    /// <summary><c>ntpath.islink(path)</c>: a symbolic link (not a junction); false for anything else or a path that cannot be read.</summary>
    bool IsLink(string path);

    /// <summary><c>nt._findfirstfile(path)</c>: the name of the first directory entry the path (a <c>FindFirstFileW</c> pattern) names, as stored.</summary>
    /// <exception cref="NtPathException">Nothing matches, or the directory cannot be listed.</exception>
    string FindFirstFile(string path);
}
