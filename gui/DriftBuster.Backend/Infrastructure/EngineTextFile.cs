using System.Text;

using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>UTF-8 file reads and writes. A directory is refused with <see cref="UnauthorizedAccessException"/>; other failures are the runtime's exceptions.</summary>
internal static class EngineTextFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Opens the kernel path for read or create; a test seam that can return a stream failing after the open.</summary>
    internal static Func<string, FileMode, Stream> OpenStream { get; set; } = DefaultOpen;

    /// <summary>Writes text already laid out with the platform's line breaks as UTF-8.</summary>
    /// <exception cref="ArgumentException">The path holds NUL.</exception>
    public static void WriteText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        WriteBytes(path, Utf8.GetBytes(text));
    }

    /// <summary><see cref="WriteText"/> for encoded bytes, naming a directory as <paramref name="shown"/> (the path when null).</summary>
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

    /// <summary>The whole file as strict UTF-8; invalid bytes throw <see cref="InvalidDataException"/>.</summary>
    /// <exception cref="ArgumentException">The path holds NUL.</exception>
    public static string ReadUtf8Text(string path) => EngineUtf8.DecodeFile(ReadBytes(path, path));

    /// <summary>The file's bytes, with <see cref="ReadUtf8Text"/>'s failures, naming a directory as <paramref name="shown"/>.</summary>
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
