using System.Text;

using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// <c>Path.write_text(text, encoding="utf-8")</c> and <c>Path.read_text(encoding="utf-8")</c>. Failures surface as the runtime's own
/// exceptions: a directory is refused with <see cref="UnauthorizedAccessException"/>, as the runtime refuses to open one.
/// </summary>
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
    /// <see cref="UnauthorizedAccessException"/> and any other failure the runtime's exception.
    /// </summary>
    /// <exception cref="ArgumentException">The path holds a NUL character.</exception>
    public static void WriteText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        WriteBytes(path, Utf8.GetBytes(text));
    }

    /// <summary>
    /// <see cref="WriteText"/> over bytes already encoded (<c>Path.write_bytes</c>, <c>open(path, "w")</c> of encoded text), naming a
    /// directory as <paramref name="shown"/> (<c>str(Path(path))</c> when null).
    /// </summary>
    public static void WriteBytes(string path, byte[] bytes, string? shown = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(bytes);
        FileSystemError.ThrowIfEmbeddedNull(path);
        shown ??= LexicalPath.Str(path);
        if (RunProfileStore.IsDirectory(path))
        {
            throw FileSystemError.AccessDenied(shown);
        }

        var stream = OpenStream(EnginePath.KernelPath(path), FileMode.Create);
        try
        {
            using (stream)
            {
                stream.Write(bytes);
            }
        }
        catch (ArgumentOutOfRangeException exc) when (!OperatingSystem.IsWindows())
        {
            // The runtime reports EFBIG from a write as ArgumentOutOfRangeException on Unix.
            throw new IOException("The file is too large.", exc);
        }
    }

    /// <summary>
    /// <c>Path(path).read_text(encoding="utf-8")</c>: the whole file as strict UTF-8. A directory raises
    /// <see cref="UnauthorizedAccessException"/>, any other failure the runtime's exception; bytes that are not UTF-8 raise
    /// <see cref="InvalidDataException"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The path holds a NUL character.</exception>
    public static string ReadUtf8Text(string path) => EngineUtf8.Decode(ReadBytes(path, path));

    /// <summary>
    /// <c>open(path, "rb").read()</c> with the failures <see cref="ReadUtf8Text"/> raises, naming a directory as <paramref name="shown"/>.
    /// </summary>
    public static byte[] ReadBytes(string path, string shown)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(shown);
        FileSystemError.ThrowIfEmbeddedNull(path);
        if (RunProfileStore.IsDirectory(path))
        {
            throw FileSystemError.AccessDenied(shown);
        }

        var stream = OpenStream(EnginePath.KernelPath(path), FileMode.Open);
        try
        {
            using (stream)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }
        catch (ArgumentOutOfRangeException exc) when (!OperatingSystem.IsWindows())
        {
            // The runtime reports EFBIG from a write as ArgumentOutOfRangeException on Unix.
            throw new IOException("The file is too large.", exc);
        }
    }

    private static FileStream DefaultOpen(string path, FileMode mode)
        => mode == FileMode.Open
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
}
