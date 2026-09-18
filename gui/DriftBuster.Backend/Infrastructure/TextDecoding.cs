using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Decodes file bytes by their byte order mark: UTF-8, UTF-16 and UTF-32 (either byte order) when one is present, the
/// caller's fallback decoder otherwise. The mark itself is not part of the text. Invalid sequences are replaced.
/// </summary>
public static class TextDecoding
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf16Le = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf32Le = new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: false);
    private static readonly Encoding Utf32Be = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: false);

    public static string Decode(ReadOnlySpan<byte> bytes, Encoding fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        var (encoding, markLength) = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => (Utf8, 3),
            [0xFF, 0xFE, 0x00, 0x00, ..] => (Utf32Le, 4),
            [0x00, 0x00, 0xFE, 0xFF, ..] => (Utf32Be, 4),
            [0xFF, 0xFE, ..] => (Utf16Le, 2),
            [0xFE, 0xFF, ..] => (Utf16Be, 2),
            _ => (fallback, 0),
        };
        return encoding.GetString(bytes[markLength..]);
    }
}
