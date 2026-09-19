using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>Writes a whole file through a temporary sibling and a rename, so a crash never leaves a half-written file behind.</summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>UTF-8 without a byte order mark; the parent directory must exist.</summary>
    public static void WriteAllText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(text);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, Utf8);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
