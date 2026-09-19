using System.Numerics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Dict key equality and key ordering for the values a binary plist can hold.</summary>
internal static partial class BinaryPlist
{
    // Stands in for "every key shares the first key's kind" while null is itself a key value.
    private static readonly object NoMismatch = new();

    /// <summary>
    /// A dict payload's keys in ascending order (empty for other payloads); a null key comes back as null. Strings by code point,
    /// numbers by value (bools as 0/1, NaN first), bytes by content, dates by instant; stable for equal keys.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Keys of mixed kinds (or including null or a UID), naming the first two types that cannot be ordered; the plugin lets it escape.
    /// </exception>
    public static List<object?> SortedKeys(object? payload)
    {
        if (payload is not OrderedDictionary<object, object?> dict)
        {
            return [];
        }

        var keys = dict.Keys.Select(key => ReferenceEquals(key, NoneKey) ? null : key).ToList();
        if (keys.Count < 2)
        {
            return keys;
        }

        var category = CategoryOf(keys[0]);
        var mismatch = keys.Skip(1).FirstOrDefault(key => category == Category.Unordered || CategoryOf(key) != category, NoMismatch);
        if (!ReferenceEquals(mismatch, NoMismatch))
        {
            throw new InvalidOperationException(
                $"A key of type '{ValueTypeName(keys[0])}' cannot be compared with a key of type '{ValueTypeName(mismatch)}'.");
        }

        return keys.Order(Comparer<object?>.Create((left, right) => CompareKeys(category, left!, right!))).ToList();
    }

    private static int CompareKeys(Category category, object left, object right) => category switch
    {
        Category.Str => PathText.CompareCodePoints((string)left, (string)right),
        Category.Number => CompareNumbers(left, right),
        Category.Bytes => ((byte[])left).AsSpan().SequenceCompareTo((byte[])right),
        _ => ((DateTime)left).CompareTo((DateTime)right),
    };

    // A total order over numbers: NaN before every other value, integers against reals exactly.
    private static int CompareNumbers(object left, object right)
    {
        if (left is double a && right is double b)
        {
            return a.CompareTo(b);
        }

        if (left is double onlyLeft)
        {
            return double.IsNaN(onlyLeft) ? -1 : -CompareIntegerToDouble(ToInteger(right), onlyLeft);
        }

        if (right is double onlyRight)
        {
            return double.IsNaN(onlyRight) ? 1 : CompareIntegerToDouble(ToInteger(left), onlyRight);
        }

        return ToInteger(left).CompareTo(ToInteger(right));
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
        null => "null",
        string => "string",
        bool => "boolean",
        BigInteger => "integer",
        double => "real",
        byte[] => "data",
        DateTime => "date",
        Uid => "UID",
        _ => key.GetType().Name,
    };

    // A boolean counts as the integer 0 or 1, so true and 1 are one key.
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

    /// <summary>Dict key equality: identity first, then value equality within compatible types.</summary>
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
