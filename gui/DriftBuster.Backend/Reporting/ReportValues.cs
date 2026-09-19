using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Reporting;

/// <summary>
/// Value helpers the reporting writers share. Mappings are <see cref="IReadOnlyDictionary{TKey, TValue}"/> keyed by string (copies are
/// <see cref="OrderedDictionary{TKey, TValue}"/>), lists are <see cref="List{T}"/>, tuples are <see cref="object"/> arrays.
/// </summary>
internal static class ReportValues
{
    /// <summary>&amp;, &lt;, &gt;, the double quote and the single quote; nothing else.</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.AsSpan().IndexOfAny("&<>\"'") < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            _ = ch switch
            {
                '&' => builder.Append("&amp;"),
                '<' => builder.Append("&lt;"),
                '>' => builder.Append("&gt;"),
                '"' => builder.Append("&quot;"),
                '\'' => builder.Append("&#x27;"),
                _ => builder.Append(ch),
            };
        }

        return builder.ToString();
    }

    /// <summary>The value's text (<see cref="EngineRepr.Str"/>), with a top-level tuple spelled <c>(a, b)</c> or <c>(a,)</c>.</summary>
    public static string Str(object? value) => value is object[] tuple ? TupleRepr(tuple) : EngineRepr.Str(ReprDomain(value));

    private static string TupleRepr(object[] tuple)
    {
        var items = string.Join(", ", tuple.Select(item => item is object[] inner ? TupleRepr(inner) : EngineRepr.Repr(ReprDomain(item))));
        return tuple.Length == 1 ? "(" + items + ",)" : "(" + items + ")";
    }

    // The list and dict shapes EngineRepr spells: any other string-keyed mapping becomes an ordered dictionary and any other list a
    // list of objects, recursively. A tuple nested inside a list or dict is outside what EngineRepr spells and raises there.
    private static object? ReprDomain(object? value) => value switch
    {
        null or string or object[] or byte[] => value,
        IReadOnlyDictionary<string, object?> or IDictionary => AsMapping(value)!.Aggregate(
            new OrderedDictionary<string, object?>(StringComparer.Ordinal),
            (copy, pair) =>
            {
                copy[pair.Key] = ReprDomain(pair.Value);
                return copy;
            }),
        IList list => list.Cast<object?>().Select(ReprDomain).ToList(),
        _ => value,
    };

    /// <summary>Any string-keyed mapping, or other dictionary copied with its keys as text.</summary>
    public static bool IsMapping(object? value, out IReadOnlyDictionary<string, object?> mapping)
    {
        var found = AsMapping(value);
        mapping = found ?? EmptyMapping;
        return found is not null;
    }

    // A string-keyed mapping as is; any other dictionary (Dictionary<string, string>, a Hashtable) as an ordered copy keyed by the key's text.
    private static IReadOnlyDictionary<string, object?>? AsMapping(object? value)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> mapping:
                return mapping;
            case IDictionary dictionary:
                var copy = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    copy[EngineRepr.Str(entry.Key)] = entry.Value;
                }

                return copy;
            default:
                return null;
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyMapping = new OrderedDictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>A shallow copy in the mapping's order.</summary>
    public static OrderedDictionary<string, object?> Copy(IEnumerable<KeyValuePair<string, object?>> mapping)
    {
        var copy = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in mapping)
        {
            copy[key] = value;
        }

        return copy;
    }

    /// <summary>
    /// Merges into <c>payload["run_metadata"]</c>, creating it: a mapping already stored there is updated in place (the caller's own
    /// dictionary, as the shallow copies share it); any other stored value raises <see cref="InvalidDataException"/>.
    /// </summary>
    public static void MergeRunMetadata(OrderedDictionary<string, object?> payload, IEnumerable<KeyValuePair<string, object?>> extra)
    {
        if (!payload.TryGetValue("run_metadata", out var existing))
        {
            existing = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            payload["run_metadata"] = existing;
        }

        switch (existing)
        {
            case IDictionary<string, object?> { IsReadOnly: false } target:
                foreach (var (key, value) in extra.ToList())
                {
                    target[key] = value;
                }

                return;
            case IDictionary { IsReadOnly: false, IsFixedSize: false } untyped:
                foreach (var (key, value) in extra.ToList())
                {
                    untyped[key] = value;
                }

                return;
            default:
                throw new InvalidDataException($"expected a JSON object, not '{TypeName(existing)}'");
        }
    }

    /// <summary>The items of a string, list, tuple or mapping; anything else raises <see cref="InvalidDataException"/>.</summary>
    public static IEnumerable<object?> Iterate(object? value) => EngineBuiltins.Iterate(value);

    public static bool Truthy(object? value) => EngineBuiltins.IsTruthy(value);

    /// <summary>The candidate only when it compares greater (a NaN never does).</summary>
    public static double Max(double current, double candidate) => candidate > current ? candidate : current;

    /// <summary>The value as a double, 0.0 when it is falsy or not a number.</summary>
    public static double FloatOrZero(object? value)
    {
        try
        {
            return EngineBuiltins.Float(Truthy(value) ? value : 0.0);
        }
        catch (Exception exc) when (exc is InvalidDataException or FormatException)
        {
            return 0.0;
        }
    }

    /// <summary>
    /// Fixed-point: the exact binary value rounded half to even at the requested digit, every integer digit written out, the sign kept on
    /// a negative value that rounds to zero; <c>nan</c>, <c>inf</c> and <c>-inf</c> in lower case.
    /// </summary>
    public static string FormatFixed(double value, int precision)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        var negative = double.IsNegative(value);
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
        var exponentBits = (int)((bits >> 52) & 0x7FF);
        var mantissa = new BigInteger(bits & 0xFFFFFFFFFFFFFL);
        var exponent = exponentBits == 0 ? -1074 : exponentBits - 1075;
        if (exponentBits != 0)
        {
            mantissa += BigInteger.One << 52;
        }

        var scaled = mantissa * BigInteger.Pow(10, precision);
        BigInteger rounded;
        if (exponent >= 0)
        {
            rounded = scaled << exponent;
        }
        else
        {
            var denominator = BigInteger.One << -exponent;
            rounded = BigInteger.DivRem(scaled, denominator, out var remainder);
            var twice = remainder << 1;
            if (twice > denominator || (twice == denominator && !rounded.IsEven))
            {
                rounded += BigInteger.One;
            }
        }

        var digits = rounded.ToString(CultureInfo.InvariantCulture).PadLeft(precision + 1, '0');
        var integerPart = digits[..^precision];
        var text = precision == 0 ? integerPart : integerPart + "." + digits[^precision..];
        return negative ? "-" + text : text;
    }

    /// <summary>
    /// The value as the JSON domain <see cref="Canonicaliser.Dumps"/> writes: mappings become ordered dictionaries, lists and tuples
    /// become lists; a value JSON cannot hold raises <see cref="NotSupportedException"/>.
    /// </summary>
    public static object? ToJsonValue(object? value) => value switch
    {
        null or string or bool or int or long or BigInteger or double => value,
        IReadOnlyDictionary<string, object?> or IDictionary => AsMapping(value)!.Aggregate(
            new OrderedDictionary<string, object?>(StringComparer.Ordinal),
            (copy, pair) =>
            {
                copy[pair.Key] = ToJsonValue(pair.Value);
                return copy;
            }),
        IList list and not byte[] => list.Cast<object?>().Select(ToJsonValue).ToList(),
        _ => throw new NotSupportedException($"A value of type '{TypeName(value)}' cannot be written as JSON."),
    };

    /// <summary>JSON at the given indent: the shared two-space layout re-indented (string contents escape their line breaks).</summary>
    public static string DumpsIndented(object? value, int indent, bool ensureAscii) => DumpsIndented(value, indent, ensureAscii, sortKeys: false);

    /// <summary><see cref="DumpsIndented(object?, int, bool)"/> with optional key sorting.</summary>
    public static string DumpsIndented(object? value, int indent, bool ensureAscii, bool sortKeys)
    {
        var text = Canonicaliser.Dumps(ToJsonValue(value), indent: true, ensureAscii, sortKeys);
        if (indent == 2)
        {
            return text;
        }

        var unit = new string(' ', Math.Max(indent, 0));
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var ch = text[index++];
            builder.Append(ch);
            if (ch != '\n')
            {
                continue;
            }

            var spaces = 0;
            while (index < text.Length && text[index] == ' ')
            {
                spaces++;
                index++;
            }

            builder.Insert(builder.Length, unit, spaces / 2);
        }

        return builder.ToString();
    }

    /// <summary>A text-mode write: each LF becomes the platform's line break.</summary>
    public static string TextModeNewLines(string text)
        => string.Equals(Environment.NewLine, "\n", StringComparison.Ordinal) ? text : text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);

    private static string TypeName(object? value) => value switch
    {
        object[] => "array",
        IEnumerable and not string when value.GetType().GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ISet<>)) => "set",
        _ => EngineBuiltins.TypeName(value),
    };
}
