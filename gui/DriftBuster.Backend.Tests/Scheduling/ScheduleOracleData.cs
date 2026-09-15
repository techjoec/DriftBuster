using System.Globalization;
using System.Numerics;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Scheduling;

namespace DriftBuster.Backend.Tests.Scheduling;

/// <summary>
/// CPython oracle data written by <c>tools/parity/gen_schedule_cases.py</c> (<c>Data/schedule_cases.json</c>), read with
/// <see cref="PythonJson"/> so the case inputs keep their Python types and unpaired surrogates.
/// </summary>
internal static class ScheduleOracleData
{
    private static readonly Lazy<OrderedDictionary<string, object?>> Data = new(() =>
    {
        var path = Path.Combine(RepoPaths.Root, "gui", "DriftBuster.Backend.Tests", "Scheduling", "Data", "schedule_cases.json");
        return PythonJson.TryLoads(File.ReadAllText(path), out var value)
            ? (OrderedDictionary<string, object?>)value!
            : throw new InvalidDataException($"{path} is not valid JSON");
    });

    public static IEnumerable<OrderedDictionary<string, object?>> Cases(string section)
        => ((List<object?>)Data.Value[section]!).Cast<OrderedDictionary<string, object?>>();

    public static OrderedDictionary<string, object?> Map(object? value) => (OrderedDictionary<string, object?>)value!;

    public static List<object?> List(object? value) => (List<object?>)value!;

    public static long Long(object? value) => value switch
    {
        int number => number,
        long number => number,
        BigInteger number => (long)number,
        _ => throw new InvalidCastException($"not an int: {value}"),
    };

    /// <summary>The C# exception type standing for a Python exception class name.</summary>
    public static Type ExceptionType(string pythonType) => pythonType switch
    {
        "ValueError" => typeof(PythonValueException),
        "OverflowError" => typeof(OverflowException),
        "ScheduleError" => typeof(ScheduleException),
        "TypeError" => typeof(PythonTypeException),
        _ => throw new InvalidDataException("unmapped Python exception " + pythonType),
    };

    /// <summary>
    /// Runs <paramref name="action"/> and describes the difference from the case's <c>result</c> (compared by <paramref name="compare"/>) or
    /// <c>error</c> (type and message); null when they agree.
    /// </summary>
    public static string? Mismatch<T>(OrderedDictionary<string, object?> entry, Func<T> action, Func<T, object?, string?> compare)
    {
        T actual;
        try
        {
            actual = action();
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            if (entry.TryGetValue("error", out var error))
            {
                var expected = Map(error);
                var expectedType = ExceptionType((string)expected["type"]!);
                return exc.GetType() == expectedType && string.Equals(exc.Message, (string)expected["message"]!, StringComparison.Ordinal)
                    ? null
                    : $"raised {exc.GetType().Name}: {exc.Message}; expected {expected["type"]}: {expected["message"]}";
            }

            return $"raised {exc.GetType().Name}: {exc.Message}; expected {PythonRepr.Repr(entry["result"])}";
        }

        return entry.TryGetValue("error", out var missing)
            ? $"returned; expected {PythonRepr.Repr(missing)}"
            : compare(actual, entry["result"]);
    }

    /// <summary>The oracle's <c>[year, month, day, hour, minute, second, microsecond, fold, offset microseconds, isoformat]</c> row, compared.</summary>
    public static string? DateTimeMismatch(PythonDateTime actual, object? expected)
    {
        var row = List(expected);
        var offset = actual.UtcOffset();
        var fields = new object?[]
        {
            actual.Year, actual.Month, actual.Day, actual.Hour, actual.Minute, actual.Second, actual.Microsecond, actual.Fold, offset,
            actual.IsoFormat(),
        };
        var expectedFields = row.Select(item => item is string or null ? item : (object)Long(item)).ToArray();
        var actualFields = fields.Select(item => item switch
        {
            int number => (object)(long)number,
            long number => number,
            _ => item,
        }).ToArray();
        return actualFields.SequenceEqual(expectedFields)
            ? null
            : $"got [{string.Join(", ", actualFields.Select(Describe))}], expected [{string.Join(", ", expectedFields.Select(Describe))}]";
    }

    private static string Describe(object? value) => value switch
    {
        null => "None",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
