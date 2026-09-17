using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>Path.read_text(encoding="utf-8")</c> and <c>Path.write_text(text, encoding="utf-8")</c> in text mode: a read turns CRLF and CR into
/// LF, a write turns LF into the platform's line break; a byte order mark is kept as the character it decodes to.
/// </summary>
internal static class TextModeFile
{
    public static string ReadText(string path)
        => EngineTextFile.ReadUtf8Text(path).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static void WriteText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        EngineTextFile.WriteText(path, OperatingSystem.IsWindows() ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text);
    }

    /// <summary><c>path.exists()</c>: the entry, followed through links, is a file or a directory.</summary>
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary><c>Path.cwd() != root</c> as <c>Path</c> equality compares them (case-insensitively on Windows).</summary>
    public static bool SamePath(string left, string right)
        => string.Equals(
            EnginePurePath.Str(left),
            EnginePurePath.Str(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
