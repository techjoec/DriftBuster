using System.Buffers;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// Built-ins the registry code needs that the shared helpers do not cover: <c>str.upper()</c> over a whole string,
/// registry search pattern compilation and <c>type(exc).__name__</c> for the exceptions the registry code raises.
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

    /// <summary>
    /// A registry search pattern as a .NET regular expression (no options besides culture invariance). A pattern that does not
    /// parse throws <see cref="RegexParseException"/> with the .NET message.
    /// </summary>
    public static Regex Compile(string pattern) => PatternRegex.Create(pattern);

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
        RegexParseException => "PatternError",
        EngineNotImplementedException => "NotImplementedError",
        KeyNotFoundException => "KeyError",
        OverflowException => "OverflowError",
        OutOfMemoryException => "MemoryError",
        PlatformNotSupportedException or InvalidOperationException => "RuntimeError",
        IOException { HResult: > 0 and < 4096 } => OsError.TypeName(exc.HResult),
        IOException => "OSError",
        _ => exc.GetType().Name,
    };
}
