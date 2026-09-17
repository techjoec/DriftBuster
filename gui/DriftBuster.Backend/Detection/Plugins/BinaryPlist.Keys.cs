using System.Numerics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Python dict key equality and <c>sorted()</c> ordering for the values a binary plist can hold.</summary>
internal static partial class BinaryPlist
{
    /// <summary>
    /// <c>sorted(payload.keys()) if isinstance(payload, dict) else []</c>, sorted by <see cref="EngineSort{T}"/> with
    /// Python's <c>&lt;</c>, so NaN keys land where CPython's sort leaves them. A key of <c>None</c> comes back as null.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The <c>TypeError</c> Python raises at the first comparison of two keys with no <c>&lt;</c> between them (str
    /// against a number, any UID or None), naming that comparison's operand types. The plugin does not catch
    /// it, so it escapes <c>detect</c>.
    /// </exception>
    public static List<object?> SortedKeys(object? payload)
    {
        if (payload is not OrderedDictionary<object, object?> dict)
        {
            return [];
        }

        var keys = dict.Keys.Select(key => ReferenceEquals(key, NoneKey) ? null : key).ToList();
        EngineSort<object?>.Sort(keys, LessThan);
        return keys;
    }

    // Python's left < right for plist key values.
    private static bool LessThan(object? left, object? right)
    {
        var category = CategoryOf(left);
        if (category == Category.Unordered || CategoryOf(right) != category)
        {
            throw new InvalidOperationException(
                $"'<' not supported between instances of '{ValueTypeName(left)}' and '{ValueTypeName(right)}'");
        }

        return category switch
        {
            Category.Str => PathText.CompareCodePoints((string)left!, (string)right!) < 0,
            Category.Number => NumberLessThan(left!, right!),
            Category.Bytes => ((byte[])left!).AsSpan().SequenceCompareTo((byte[])right!) < 0,
            _ => (DateTime)left! < (DateTime)right!,
        };
    }

    // Every ordering against NaN is false; int against float compares exactly, never through a lossy conversion.
    private static bool NumberLessThan(object left, object right)
    {
        if (left is double a && right is double b)
        {
            return a < b;
        }

        if (left is double onlyLeft)
        {
            return !double.IsNaN(onlyLeft) && CompareIntegerToDouble(ToInteger(right), onlyLeft) > 0;
        }

        if (right is double onlyRight)
        {
            return !double.IsNaN(onlyRight) && CompareIntegerToDouble(ToInteger(left), onlyRight) < 0;
        }

        return ToInteger(left) < ToInteger(right);
    }

    private enum Category
    {
        Str,
        Number,
        Bytes,
        Date,
        Unordered,
    }

    private static Category CategoryOf(object? key) => key switch
    {
        string => Category.Str,
        bool or BigInteger or double => Category.Number,
        byte[] => Category.Bytes,
        DateTime => Category.Date,
        _ => Category.Unordered,
    };

    private static string ValueTypeName(object? key) => key switch
    {
        null => "NoneType",
        string => "str",
        bool => "bool",
        BigInteger => "int",
        double => "float",
        byte[] => "bytes",
        DateTime => "datetime.datetime",
        Uid => "UID",
        _ => key.GetType().Name,
    };

    // bool is an int subclass.
    private static BigInteger ToInteger(object value) => value switch
    {
        bool flag => flag ? BigInteger.One : BigInteger.Zero,
        BigInteger number => number,
        _ => throw new ArgumentException("Not an integer", nameof(value)),
    };

    // The sign of integer - real for a real that is not NaN (callers rule NaN out: every comparison with it is false).
    private static int CompareIntegerToDouble(BigInteger integer, double real)
    {
        if (double.IsNaN(real))
        {
            return 0;
        }

        if (double.IsInfinity(real))
        {
            return real > 0 ? -1 : 1;
        }

        var floor = new BigInteger(Math.Floor(real));
        var result = integer.CompareTo(floor);
        return result != 0 ? result : (real > Math.Floor(real) ? -1 : 0);
    }

    private static bool NumericEquals(object left, object right)
    {
        if (left is double a && right is double b)
        {
            return a == b;
        }

        if (left is double onlyLeft)
        {
            return double.IsFinite(onlyLeft) && CompareIntegerToDouble(ToInteger(right), onlyLeft) == 0;
        }

        if (right is double onlyRight)
        {
            return double.IsFinite(onlyRight) && CompareIntegerToDouble(ToInteger(left), onlyRight) == 0;
        }

        return ToInteger(left) == ToInteger(right);
    }

    private static int NumericHash(object value) => value switch
    {
        double real => double.IsNaN(real) ? 0 : real.GetHashCode(),
        _ => ((double)ToInteger(value)).GetHashCode(),
    };

    /// <summary>Python dict key semantics: identity first, then value equality within compatible types.</summary>
    private sealed class KeyComparer : IEqualityComparer<object>
    {
        public static KeyComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            return (x, y) switch
            {
                (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
                (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
                (DateTime a, DateTime b) => a == b,
                (Uid a, Uid b) => a.Data == b.Data,
                _ => CategoryOf(x) == Category.Number && CategoryOf(y) == Category.Number && NumericEquals(x, y),
            };
        }

        public int GetHashCode(object obj) => obj switch
        {
            string text => StringComparer.Ordinal.GetHashCode(text),
            byte[] bytes => bytes.Length,
            DateTime date => date.GetHashCode(),
            Uid uid => uid.Data.GetHashCode(),
            bool or BigInteger or double => NumericHash(obj),
            _ => 0,
        };
    }
}
