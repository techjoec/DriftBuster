using System.Text;

using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Infrastructure;

/// <summary><c>Path.write_text(text, encoding="utf-8")</c> and <c>Path.read_text(encoding="utf-8")</c> with Python's <c>OSError</c> texts.</summary>
/// <remarks>
/// Python names the file only in an error <c>open()</c> raises (<c>[Errno 13] Permission denied: 'x'</c>); a read or write that fails
/// once the file is open (<c>ENOSPC</c>, <c>EDQUOT</c>, <c>EIO</c>, <c>EFBIG</c>) raises the bare <c>[Errno 28] No space left on device</c>.
/// The file is therefore opened through <see cref="OpenStream"/> first and read or written afterwards, each step with its own text.
/// </remarks>
internal static class EngineTextFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Opens the kernel path for reading (<see cref="FileMode.Open"/>) or writing (<see cref="FileMode.Create"/>); a test seam that hands
    /// back a stream failing after the open.
    /// </summary>
    internal static Func<string, FileMode, Stream> OpenStream { get; set; } = DefaultOpen;

    /// <summary>
    /// <c>Path(path).write_text(text, encoding="utf-8")</c> for text already laid out with the platform's line breaks: a directory raises
    /// <c>open()</c>'s error for one (<see cref="OsError.DirectoryOpenErrno"/>) and any other failure Python's <c>OSError</c> text
    /// (<see cref="OsError"/>), the path spelled as <c>str(Path)</c> spells it when <c>open()</c> raised, and no path when the write did.
    /// </summary>
    /// <exception cref="EngineValueException">The path holds a NUL character (<c>open()</c>'s <c>embedded null byte</c>).</exception>
    public static void WriteText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        WriteBytes(path, Utf8.GetBytes(text));
    }

    /// <summary>
    /// <see cref="WriteText"/> over bytes already encoded (<c>Path.write_bytes</c>, <c>open(path, "w")</c> of encoded text), naming the file
    /// in an <c>open()</c> error as <paramref name="shown"/> (<c>str(Path(path))</c> when null).
    /// </summary>
    public static void WriteBytes(string path, byte[] bytes, string? shown = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(bytes);
        OsError.ThrowIfEmbeddedNull(path);
        shown ??= LexicalPath.Str(path);
        if (RunProfileStore.IsDirectory(path))
        {
            throw OsError.Create(OsError.DirectoryOpenErrno, shown);
        }

        var stream = Open(path, shown, FileMode.Create);
        try
        {
            using (stream)
            {
                stream.Write(bytes);
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException || (exc is ArgumentOutOfRangeException && !OperatingSystem.IsWindows()))
        {
            throw AfterOpenError(exc);
        }
    }

    /// <summary>
    /// <c>Path(path).read_text(encoding="utf-8")</c>: the whole file as strict UTF-8. A directory raises <c>open()</c>'s error for one
    /// (<see cref="OsError.DirectoryOpenErrno"/>), a failed open Python's <c>OSError</c> text naming the path and a failed read the
    /// text without it; bytes that are not UTF-8 raise <see cref="EngineUnicodeDecodeException"/>.
    /// </summary>
    /// <exception cref="EngineValueException">The path holds a NUL character (<c>open()</c>'s <c>embedded null byte</c>).</exception>
    public static string ReadUtf8Text(string path) => EngineUtf8.Decode(ReadBytes(path, path));

    /// <summary>
    /// <c>open(path, "rb").read()</c> with Python's texts, as <see cref="ReadUtf8Text"/> raises them, naming the file as
    /// <paramref name="shown"/>.
    /// </summary>
    public static byte[] ReadBytes(string path, string shown)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(shown);
        OsError.ThrowIfEmbeddedNull(path);
        try
        {
            if (RunProfileStore.IsDirectory(path))
            {
                throw OsError.Create(OsError.DirectoryOpenErrno, shown);
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw OpenError(exc, path, shown);
        }

        var stream = Open(path, shown, FileMode.Open);
        try
        {
            using (stream)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException || (exc is ArgumentOutOfRangeException && !OperatingSystem.IsWindows()))
        {
            throw AfterOpenError(exc);
        }
    }

    private static Stream Open(string path, string shown, FileMode mode)
    {
        try
        {
            return OpenStream(EnginePath.KernelPath(path), mode);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw OpenError(exc, path, shown);
        }
    }

    private static FileStream DefaultOpen(string path, FileMode mode)
        => mode == FileMode.Open
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    // open() failed: Python's OSError text for the path, unless the exception already carries it or no errno is known.
    private static Exception OpenError(Exception exc, string path, string shown)
        => exc.Message.StartsWith("[Errno ", StringComparison.Ordinal) || OsError.Errno(exc, path) is not { } known
            ? exc
            : OsError.Create(known, shown, exc);

    // A read or write on the open file failed: Python's OSError text without a file name.
    // The runtime reports EFBIG from a write as ArgumentOutOfRangeException on Unix.
    private static Exception AfterOpenError(Exception exc)
        => exc is ArgumentOutOfRangeException
            ? OsError.Create(OsError.FileTooLarge, exc)
            : OsError.Errno(exc) is { } known ? OsError.Create(known, exc) : exc;
}
