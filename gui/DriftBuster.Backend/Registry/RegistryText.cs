using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>Registry text helpers: full-string uppercase and search pattern compilation.</summary>
internal static class RegistryText
{
    /// <summary>Full uppercase per code point (<see cref="EngineText.Upper(Rune)"/>); unpaired surrogates pass through.</summary>
    public static string Upper(string text)
    {
        var builder = new StringBuilder(text.Length);
        var offset = 0;
        while (offset < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out var consumed) != OperationStatus.Done)
            {
                builder.Append(text[offset]);
                offset++;
                continue;
            }

            builder.Append(EngineText.Upper(rune));
            offset += consumed;
        }

        return builder.ToString();
    }

    /// <summary>
    /// A registry search pattern as a .NET regular expression (no options besides culture invariance). A pattern that does not
    /// parse throws <see cref="RegexParseException"/> with the .NET message.
    /// </summary>
    public static Regex Compile(string pattern) => PatternRegex.Create(pattern);
}
