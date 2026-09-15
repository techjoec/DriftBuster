using System.Collections;
using System.Numerics;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python <c>==</c>, <c>hash()</c> and <c>&lt;</c> over the values <see cref="PythonJson"/> produces: bool, int and float compare
/// as numbers (<c>True == 1 == 1.0</c>), str by code point, lists element by element and dicts by their items, each element
/// compared by identity first as CPython's containers do. Lists and dicts are unhashable, and <c>&lt;</c> exists only between two
/// strs or two numbers.
/// </summary>
public static class PythonValues
{
    /// <summary>Set and dict key semantics: <see cref="Equal"/>, and <c>TypeError: unhashable type</c> for a list or dict.</summary>
    public static IEqualityComparer<object?> HashKeys { get; } = new HashKeyComparer();

    /// <summary><c>left == right</c>.</summary>
    public static bool Equal(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return CompareNumbers(left, right) == 0;
        }

        return (left, right) switch
        {
            (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
            (IReadOnlyDictionary<string, object?> a, IReadOnlyDictionary<string, object?> b) => DictsEqual(a, b),
            (string, _) or (_, string) => false,
            (IList a, IList b) => a.Count == b.Count && Enumerable.Range(0, a.Count).All(index => Equal(a[index], b[index])),
            _ => false,
        };
    }

    /// <summary><c>left &lt; right</c>; <c>TypeError</c> (<see cref="PythonTypeException"/>) when the two have no ordering.</summary>
    public static bool LessThan(object? left, object? right)
    {
        if (left is string a && right is string b)
        {
            return PathText.CompareCodePoints(a, b) < 0;
        }

        if (left is not null && right is not null && IsNumber(left) && IsNumber(right))
        {
            return CompareNumbers(left, right) < 0;
        }

        throw new PythonTypeException(
            $"'<' not supported between instances of '{PythonBuiltins.TypeName(left)}' and '{PythonBuiltins.TypeName(right)}'",
            nameof(left));
    }

    /// <summary><c>sorted(items)</c> with <see cref="LessThan"/>, in CPython's comparison order.</summary>
    public static IReadOnlyList<object?> Sorted(IEnumerable<object?> items)
    {
        var list = items.ToList();
        PythonSort<object?>.Sort(list, LessThan);
        return list;
    }

    /// <summary>A Python int as the smallest of <see cref="int"/>, <see cref="long"/> and <see cref="BigInteger"/> that holds it.</summary>
    public static object Narrow(BigInteger value)
    {
        if (value >= int.MinValue && value <= int.MaxValue)
        {
            return (int)value;
        }

        return value >= long.MinValue && value <= long.MaxValue ? (long)value : (object)value;
    }

    private static bool DictsEqual(IReadOnlyDictionary<string, object?> left, IReadOnlyDictionary<string, object?> right)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var other) && Equal(pair.Value, other));

    private static bool IsNumber(object value) => value is bool or int or long or BigInteger or double;

    // The sign of left - right for two numbers; NaN is unordered, so every comparison with it reports "not equal, not less".
    private static int CompareNumbers(object left, object right)
    {
        if (left is double a && right is double b)
        {
            return double.IsNaN(a) || double.IsNaN(b) ? 2 : a.CompareTo(b);
        }

        if (left is double onlyLeft)
        {
            return double.IsNaN(onlyLeft) ? 2 : -CompareIntegerToDouble(ToInteger(right), onlyLeft);
        }

        return right is double onlyRight
            ? (double.IsNaN(onlyRight) ? 2 : CompareIntegerToDouble(ToInteger(left), onlyRight))
            : ToInteger(left).CompareTo(ToInteger(right));
    }

    private static BigInteger ToInteger(object value) => value switch
    {
        bool flag => flag ? BigInteger.One : BigInteger.Zero,
        int number => number,
        long number => number,
        _ => (BigInteger)value,
    };

    // The sign of integer - real for a real that is not NaN, exact for integers past double precision.
    private static int CompareIntegerToDouble(BigInteger integer, double real)
    {
        if (double.IsInfinity(real))
        {
            return real > 0 ? -1 : 1;
        }

        var floor = Math.Floor(real);
        var result = integer.CompareTo(new BigInteger(floor));
        return result != 0 ? result : (real > floor ? -1 : 0);
    }

    private sealed class HashKeyComparer : IEqualityComparer<object?>
    {
        public new bool Equals(object? x, object? y) => Equal(x, y);

        public int GetHashCode(object? obj) => obj switch
        {
            null => 0,
            string text => StringComparer.Ordinal.GetHashCode(text),
            double real => double.IsNaN(real) ? 0 : real.GetHashCode(),
            bool or int or long or BigInteger => ((double)ToInteger(obj)).GetHashCode(),
            _ => throw new PythonTypeException($"unhashable type: '{PythonBuiltins.TypeName(obj)}'", nameof(obj)),
        };
    }
}
