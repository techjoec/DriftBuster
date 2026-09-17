using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.EngineRe;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// Built-ins the registry code needs that the shared helpers do not cover: <c>str.upper()</c> over a whole string,
/// <c>re.split</c>, <c>re.compile</c>'s error text and <c>type(exc).__name__</c> for the exceptions the registry code raises.
/// </summary>
internal static class RegistryText
{
    /// <summary><c>text.upper()</c>: <see cref="EngineText.Upper(Rune)"/> per code point, an unpaired surrogate passed through.</summary>
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

    /// <summary><c>re.split(pattern, text)</c> for a pattern without groups that never matches the empty string.</summary>
    public static List<string> Split(EnginePattern pattern, string text)
    {
        var parts = new List<string>();
        var start = 0;
        foreach (var match in pattern.FindIter(text))
        {
            parts.Add(text[start..match.Start]);
            start = match.End;
        }

        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>
    /// <c>re.compile(pattern)</c>. A pattern that does not compile raises <see cref="EngineReException"/> whose message is
    /// <c>str(re.error)</c>: the message, <c> at position N</c> when the compiler knows the position, and
    /// <c> (line L, column C)</c> when the pattern holds a line feed.
    /// </summary>
    public static EnginePattern Compile(string pattern)
    {
        try
        {
            return EnginePattern.Compile(pattern);
        }
        catch (EngineReException exc) when (exc.Position is { } position)
        {
            var message = string.Create(CultureInfo.InvariantCulture, $"{exc.Message} at position {position}");
            if (pattern.Contains('\n', StringComparison.Ordinal))
            {
                // lineno = pattern.count("\n", 0, pos) + 1; colno = pos - pattern.rfind("\n", 0, pos), on code point offsets.
                var before = UnitOffset(pattern, position);
                var line = pattern.AsSpan(0, before).Count('\n') + 1;
                var lastNewline = pattern.AsSpan(0, before).LastIndexOf('\n');
                var column = position - (lastNewline < 0 ? -1 : CodePoints(pattern, lastNewline));
                message = string.Create(CultureInfo.InvariantCulture, $"{message} (line {line}, column {column})");
            }

            throw new EngineReException(message, position);
        }
    }

    // The UTF-16 offset of the code point at index codePoints (an unpaired surrogate is one code point).
    private static int UnitOffset(string text, int codePoints)
    {
        var offset = 0;
        for (var count = 0; count < codePoints && offset < text.Length; count++)
        {
            offset += char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]) ? 2 : 1;
        }

        return offset;
    }

    // The code point index of the UTF-16 offset.
    private static int CodePoints(string text, int units)
    {
        var count = 0;
        for (var offset = 0; offset < units; count++)
        {
            offset += char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]) ? 2 : 1;
        }

        return count;
    }

    /// <summary>True for a value <c>isinstance(value, (list, tuple))</c> accepts: any list that is neither a str nor bytes nor a dict.</summary>
    public static bool IsList(object? value) => value is IList and not byte[] and not IDictionary && value is not IReadOnlyDictionary<string, object?>;

    /// <summary>
    /// <c>type(exc).__name__</c> of the error kind each exception stands for: <c>RuntimeError</c> is
    /// <see cref="PlatformNotSupportedException"/> (the default backend off Windows) or <see cref="InvalidOperationException"/>; any
    /// other exception keeps its runtime name.
    /// </summary>
    public static string ErrorName(Exception exc) => exc switch
    {
        CommandExitException => "SystemExit",
        EngineValueException => "ValueError",
        EngineTypeException => "TypeError",
        EngineIndexException => "IndexError",
        EngineAttributeException => "AttributeError",
        EngineRecursionException => "RecursionError",
        EngineUnicodeDecodeException => "UnicodeDecodeError",
        EngineReException => "PatternError",
        EngineNotImplementedException => "NotImplementedError",
        KeyNotFoundException => "KeyError",
        OverflowException => "OverflowError",
        OutOfMemoryException => "MemoryError",
        PlatformNotSupportedException or InvalidOperationException => "RuntimeError",
        IOException { HResult: > 0 and < 4096 } => EngineOSError.TypeName(exc.HResult),
        IOException => "OSError",
        _ => exc.GetType().Name,
    };
}
