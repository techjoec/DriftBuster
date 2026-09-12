using System.Collections;
using System.Globalization;
using System.Text;

namespace DriftBuster.Cli;

/// <summary>
/// Writes values as Python <c>json.dumps(value, sort_keys=True, ensure_ascii=False)</c> would: ", " and ": "
/// separators, keys sorted ordinally, floats in shortest round-trip form with a ".0" on integral values, and only
/// quotes, backslashes and C0 controls escaped. An unpaired surrogate is first rewritten to the six characters
/// <c>\uXXXX</c> (lower-case hex), exactly as <c>py_dump.py</c> does, because a UTF-8 stdout would replace it with
/// U+FFFD and jq rejects the JSON escape of a lone surrogate.
/// </summary>
internal static class CanonicalJson
{
    public static string Serialize(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case bool flag:
                builder.Append(flag ? "true" : "false");
                break;
            case string text:
                WriteString(builder, text);
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case float single:
                WriteDouble(builder, single);
                break;
            case double number:
                WriteDouble(builder, number);
                break;
            case decimal money:
                WriteDouble(builder, (double)money);
                break;
            case IDictionary dictionary:
                WriteObject(builder, Entries(dictionary));
                break;
            case IEnumerable enumerable when enumerable is IEnumerable<KeyValuePair<string, object?>> pairs:
                WriteObject(builder, pairs);
                break;
            case IEnumerable enumerable:
                WriteArray(builder, enumerable);
                break;
            default:
                WriteString(builder, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }

    private static IEnumerable<KeyValuePair<string, object?>> Entries(IDictionary dictionary)
    {
        foreach (DictionaryEntry entry in dictionary)
        {
            yield return new KeyValuePair<string, object?>(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty, entry.Value);
        }
    }

    private static void WriteObject(StringBuilder builder, IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        builder.Append('{');
        var first = true;
        foreach (var pair in pairs.Select(pair => new KeyValuePair<string, object?>(EscapeLoneSurrogates(pair.Key), pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(", ");
            }

            first = false;
            WriteString(builder, pair.Key);
            builder.Append(": ");
            Write(builder, pair.Value);
        }

        builder.Append('}');
    }

    private static void WriteArray(StringBuilder builder, IEnumerable items)
    {
        builder.Append('[');
        var first = true;
        foreach (var item in items)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            first = false;
            Write(builder, item);
        }

        builder.Append(']');
    }

    private static void WriteDouble(StringBuilder builder, double number)
    {
        if (double.IsNaN(number))
        {
            builder.Append("NaN");
            return;
        }

        if (double.IsInfinity(number))
        {
            builder.Append(number > 0 ? "Infinity" : "-Infinity");
            return;
        }

        builder.Append(PythonRepr(number));
    }

    /// <summary>
    /// Python <c>repr(float)</c>: shortest round-trip digits, fixed notation when the decimal exponent is in
    /// [-4, 16), otherwise "d.ddde+XX" with a signed two-digit-minimum exponent; integral values keep a ".0".
    /// </summary>
    internal static string PythonRepr(double number)
    {
        var roundTrip = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture);
        var sign = number < 0 || (number == 0 && double.IsNegative(number)) ? "-" : string.Empty;
        var split = roundTrip.IndexOf('E', StringComparison.Ordinal);
        var mantissa = split < 0 ? roundTrip : roundTrip[..split];
        var exponent = split < 0 ? 0 : int.Parse(roundTrip[(split + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        var integerDigits = point < 0 ? mantissa.Length : point;
        var trimmedLeading = digits.TrimStart('0');
        if (trimmedLeading.Length == 0)
        {
            return sign + "0.0";
        }

        // Decimal exponent of the first significant digit.
        var sciExponent = integerDigits - (digits.Length - trimmedLeading.Length) - 1 + exponent;
        digits = trimmedLeading.TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }

        if (sciExponent is >= -4 and < 16)
        {
            return sign + FixedNotation(digits, sciExponent);
        }

        var body = digits.Length == 1 ? digits : string.Concat(digits.AsSpan(0, 1), ".", digits.AsSpan(1));
        var expSign = sciExponent < 0 ? "-" : "+";
        return string.Concat(sign, body, "e", expSign, Math.Abs(sciExponent).ToString("00", CultureInfo.InvariantCulture));
    }

    private static string FixedNotation(string digits, int sciExponent)
    {
        if (sciExponent < 0)
        {
            return "0." + new string('0', -sciExponent - 1) + digits;
        }

        var integerLength = sciExponent + 1;
        if (digits.Length <= integerLength)
        {
            return digits + new string('0', integerLength - digits.Length) + ".0";
        }

        return string.Concat(digits.AsSpan(0, integerLength), ".", digits.AsSpan(integerLength));
    }

    /// <summary>Every unpaired surrogate replaced by the literal text <c>\uXXXX</c>; other text unchanged.</summary>
    internal static string EscapeLoneSurrogates(string text)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (char.IsHighSurrogate(ch) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                builder?.Append(ch).Append(text[index + 1]);
                index++;
                continue;
            }

            if (char.IsSurrogate(ch))
            {
                builder ??= new StringBuilder(text, 0, index, text.Length + 8);
                builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                continue;
            }

            builder?.Append(ch);
        }

        return builder?.ToString() ?? text;
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var ch in EscapeLoneSurrogates(text))
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (ch < ' ')
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
