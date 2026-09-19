using System.Globalization;
using System.Numerics;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Cli.Commands;

/// <summary>Console writes and the JSON and code-point text helpers the commands share.</summary>
internal static class ConsoleText
{
    /// <summary>Writes the text as is; the console streams translate line breaks (<see cref="TextModeWriter"/>).</summary>
    public static void Write(TextWriter writer, string text) => writer.Write(text);

    /// <summary>The line plus LF.</summary>
    public static void Print(TextWriter writer, string line) => Write(writer, line + "\n");

    /// <summary>
    /// JSON on one line with ", " and ": " when <paramref name="indent"/> is null, otherwise items on their own lines indented by that many
    /// spaces per level; keys optionally sorted, optionally ASCII-escaped.
    /// </summary>
    public static string Dumps(object? value, int? indent, bool sortKeys, bool ensureAscii = true)
        => indent is { } width
            ? ReportValues.DumpsIndented(value, width, ensureAscii, sortKeys)
            : Canonicaliser.Dumps(ReportValues.ToJsonValue(value), indent: false, ensureAscii, sortKeys);

    /// <summary>Length in code points.</summary>
    public static int Len(string text) => EngineBuiltins.Len(text);

    /// <summary>The first <paramref name="count"/> code points.</summary>
    public static string Head(string text, int count)
    {
        var offset = 0;
        for (var taken = 0; taken < count && offset < text.Length; taken++)
        {
            offset += char.IsSurrogatePair(text, offset) ? 2 : 1;
        }

        return text[..offset];
    }

    /// <summary>Right-padded with spaces to <paramref name="width"/> code points.</summary>
    public static string LeftJustify(string text, int width) => text + new string(' ', Math.Max(0, width - Len(text)));

    /// <summary>
    /// A <c>--sample-size</c> of any integer size for the detector: a value past <see cref="int"/> is clamped with the detector's own
    /// guardrail warning, one below it raises as any non-positive size does.
    /// </summary>
    public static int? DetectorSampleSize(BigInteger? sampleSize, Action<string> warn)
    {
        if (sampleSize is not { } size)
        {
            return null;
        }

        if (size > int.MaxValue)
        {
            warn(string.Create(
                CultureInfo.InvariantCulture,
                $"Sample size {size} exceeds {Detector.MaxSampleSize} bytes; clamping to guardrail."));
            return Detector.MaxSampleSize;
        }

        return (int)BigInteger.Max(size, int.MinValue);
    }
}
