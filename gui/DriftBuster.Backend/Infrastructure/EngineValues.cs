using System.Collections;
using System.Numerics;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python <c>==</c>, <c>hash()</c> and <c>&lt;</c> over the values <see cref="EngineJson"/> produces: bool, int and float compare
/// as numbers (<c>True == 1 == 1.0</c>), str by code point, lists and tuples (<see cref="object"/> arrays) element by element and dicts
/// by their items, each element compared by identity first as CPython's containers do. A list never equals a tuple. Lists and dicts are
/// unhashable (a tuple hashes when its items do), and <c>&lt;</c> exists only between two strs, two numbers, two lists or two tuples.
/// </summary>
public static class EngineValues
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
            (object?[] a, object?[] b) => SequencesEqual(a, b),
            (object?[], _) or (_, object?[]) => false,
            (IList a, IList b) => SequencesEqual(a, b),
            _ => false,
        };
    }

    /// <summary><c>left &lt; right</c>; <c>TypeError</c> (<see cref="EngineTypeException"/>) when the two have no ordering.</summary>
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

        if (left is object?[] leftTuple && right is object?[] rightTuple)
        {
            return SequenceLessThan(leftTuple, rightTuple);
        }

        if (left is IList leftList and not (object?[] or IDictionary) && right is IList rightList and not (object?[] or IDictionary))
        {
            return SequenceLessThan(leftList, rightList);
        }

        throw new EngineTypeException(
            $"'<' not supported between instances of '{EngineBuiltins.TypeName(left)}' and '{EngineBuiltins.TypeName(right)}'",
            nameof(left));
    }

    /// <summary><c>left &lt;= right</c>; <c>TypeError</c> (<see cref="EngineTypeException"/>) when the two have no ordering. A NaN is never <c>&lt;=</c> anything.</summary>
    public static bool LessThanOrEqual(object? left, object? right)
    {
        if (left is string a && right is string b)
        {
            return PathText.CompareCodePoints(a, b) <= 0;
        }

        if (left is not null && right is not null && IsNumber(left) && IsNumber(right))
        {
            return CompareNumbers(left, right) <= 0;
        }

        if ((left is object?[] && right is object?[])
            || (left is IList and not (object?[] or IDictionary) && right is IList and not (object?[] or IDictionary)))
        {
            return Equal(left, right) || LessThan(left, right);
        }

        throw new EngineTypeException(
            $"'<=' not supported between instances of '{EngineBuiltins.TypeName(left)}' and '{EngineBuiltins.TypeName(right)}'",
            nameof(left));
    }

    /// <summary><c>sorted(items)</c> with <see cref="LessThan"/>, in CPython's comparison order.</summary>
    public static IReadOnlyList<object?> Sorted(IEnumerable<object?> items)
    {
        var list = items.ToList();
        EngineSort<object?>.Sort(list, LessThan);
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

    private static bool SequencesEqual(IList left, IList right)
        => left.Count == right.Count && Enumerable.Range(0, left.Count).All(index => Equal(left[index], right[index]));

    // list_richcompare / tuplerichcompare: the first index whose items are not equal decides with that pair's "<"; with none, the
    // shorter sequence is less.
    private static bool SequenceLessThan(IList left, IList right)
    {
        var shared = Math.Min(left.Count, right.Count);
        for (var index = 0; index < shared; index++)
        {
            if (!Equal(left[index], right[index]))
            {
                return LessThan(left[index], right[index]);
            }
        }

        return left.Count < right.Count;
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
            object?[] tuple => tuple.Aggregate(tuple.Length, (hash, item) => HashCode.Combine(hash, GetHashCode(item))),
            _ => throw new EngineTypeException($"unhashable type: '{EngineBuiltins.TypeName(obj)}'", nameof(obj)),
        };
    }
}
