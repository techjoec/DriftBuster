using System.Globalization;
using System.Numerics;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// Console writes as the commands make them: <c>sys.stdout.write</c> and <c>print</c>, and <c>json.dumps</c> spelling values with its separators, <c>indent</c>, <c>sort_keys</c> and <c>ensure_ascii</c>.
/// </summary>
internal static class ConsoleText
{
    /// <summary><c>stream.write(text)</c>; the console streams translate line breaks (<see cref="TextModeWriter"/>).</summary>
    public static void Write(TextWriter writer, string text) => writer.Write(text);

    /// <summary><c>print(line)</c>.</summary>
    public static void Print(TextWriter writer, string line) => Write(writer, line + "\n");

    /// <summary>
    /// <c>json.dumps(value, indent=indent, sort_keys=sortKeys, ensure_ascii=ensureAscii)</c>: one line with ", " and ": " when
    /// <paramref name="indent"/> is null, otherwise items on their own lines indented by that many spaces per level.
    /// </summary>
    public static string Dumps(object? value, int? indent, bool sortKeys, bool ensureAscii = true)
        => indent is { } width
            ? ReportValues.DumpsIndented(value, width, ensureAscii, sortKeys)
            : Canonicaliser.Dumps(ReportValues.ToJsonValue(value), indent: false, ensureAscii, sortKeys);

    /// <summary><c>len(text)</c>: code points.</summary>
    public static int Len(string text) => EngineBuiltins.Len(text);

    /// <summary><c>text[:count]</c> over code points.</summary>
    public static string Head(string text, int count)
    {
        var offset = 0;
        for (var taken = 0; taken < count && offset < text.Length; taken++)
        {
            offset += char.IsSurrogatePair(text, offset) ? 2 : 1;
        }

        return text[..offset];
    }

    /// <summary><c>text.ljust(width)</c> over code points.</summary>
    public static string LeftJustify(string text, int width) => text + new string(' ', Math.Max(0, width - Len(text)));

    /// <summary>
    /// A detector sample size as <c>Detector(sample_size=...)</c> receives an <c>argparse</c> <c>int</c>: a value past the detector's
    /// <see cref="int"/> parameter is clamped with the detector's own guardrail warning, one below it raises as any non-positive size does.
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
