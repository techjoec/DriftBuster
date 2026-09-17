using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Sql;
using DriftBuster.Backend.Tests.Sql;

namespace DriftBuster.Backend.Tests.Remote;

/// <summary>
/// CPython oracle data written by <c>tools/parity/gen_capture_cases.py</c> (<c>Data/capture_cases.json</c>), read with <see cref="PythonJson"/>.
/// Every case ran in a temporary directory written as <c>{dir}</c>; <see cref="WriteTree"/> rebuilds its files under a new one.
/// </summary>
internal static partial class CaptureOracleData
{
    private static readonly Lazy<OrderedDictionary<string, object?>> Data = new(() =>
    {
        var path = Path.Combine(RepoPaths.Root, "gui", "DriftBuster.Backend.Tests", "Remote", "Data", "capture_cases.json");
        return PythonJson.TryLoads(File.ReadAllText(path), out var value)
            ? (OrderedDictionary<string, object?>)value!
            : throw new InvalidDataException($"{path} is not valid JSON");
    });

    public static List<OrderedDictionary<string, object?>> Cases(string section)
        => ((List<object?>)Data.Value[section]!).Cast<OrderedDictionary<string, object?>>().ToList();

    /// <summary>The case names of <paramref name="section"/> as theory rows.</summary>
    public static TheoryData<string> Names(string section)
    {
        var data = new TheoryData<string>();
        foreach (var entry in Cases(section))
        {
            data.Add((string)entry["name"]!);
        }

        return data;
    }

    /// <summary>The indexes of <paramref name="section"/> as theory rows, for sections without names.</summary>
    public static TheoryData<int> Indexes(string section)
    {
        var data = new TheoryData<int>();
        foreach (var index in Enumerable.Range(0, Cases(section).Count))
        {
            data.Add(index);
        }

        return data;
    }

    public static OrderedDictionary<string, object?> Case(string section, string name)
        => Cases(section).Single(entry => string.Equals((string?)entry["name"], name, StringComparison.Ordinal));

    /// <summary>A text with every <c>{dir}</c> replaced by <paramref name="directory"/>.</summary>
    public static string Expand(string text, string directory) => text.Replace("{dir}", directory, StringComparison.Ordinal);

    /// <summary>A string list option with every <c>{dir}</c> replaced.</summary>
    public static List<string> Strings(object? value, string directory)
        => ((List<object?>)value!).Select(item => Expand((string)item!, directory)).ToList();

    /// <summary>A string option (or null) with every <c>{dir}</c> replaced.</summary>
    public static string? OptionalString(object? value, string directory) => value is string text ? Expand(text, directory) : null;

    /// <summary>Writes the case's tree: text as UTF-8, <c>{"b64"}</c> as bytes, <c>{"sql"}</c> as a database built step by step.</summary>
    public static void WriteTree(object? tree, string directory)
    {
        foreach (var (relative, content) in (OrderedDictionary<string, object?>)tree!)
        {
            var path = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            switch (content)
            {
                case string text:
                    File.WriteAllText(path, text, new UTF8Encoding(false));
                    break;
                case OrderedDictionary<string, object?> bytes when bytes.TryGetValue("b64", out var encoded):
                    File.WriteAllBytes(path, Convert.FromBase64String((string)encoded!));
                    break;
                case OrderedDictionary<string, object?> database when database.TryGetValue("sql", out var steps):
                    SqlTestDatabase.Build(path, ((List<object?>)steps!).Select(SqlOracleData.Decode));
                    break;
                default:
                    throw new InvalidDataException($"unsupported tree entry {relative}");
            }
        }
    }

    /// <summary>
    /// Null when <paramref name="exc"/> is the oracle's <c>{"type", "message"}</c> error (<c>{dir}</c> replaced); otherwise the difference.
    /// </summary>
    public static string? ErrorMismatch(Exception exc, object? expected, string directory)
    {
        var error = (OrderedDictionary<string, object?>)expected!;
        var type = (string)error["type"]!;
        var message = JsonDecodeText(Expand((string)error["message"]!, directory));
        var actualType = PythonTypeName(exc);
        return string.Equals(actualType, type, StringComparison.Ordinal) && string.Equals(exc.Message, message, StringComparison.Ordinal)
            ? null
            : $"raised {actualType} ({exc.GetType().Name}): {exc.Message}; expected {type}: {message}";
    }

    /// <summary>The Python exception class the port's exception stands for.</summary>
    public static string PythonTypeName(Exception exc) => exc switch
    {
        Sqlite3Exception sqlite => sqlite.TypeName,
        PythonValueException => "ValueError",
        PythonAttributeException => "AttributeError",
        PythonTypeException => "TypeError",
        PythonIndexException => "IndexError",
        PythonRecursionException => "RecursionError",
        PythonUnicodeDecodeException => "UnicodeDecodeError",
        KeyNotFoundException => "KeyError",
        FileNotFoundException => "FileNotFoundError",
        IOException { HResult: > 0 and < 4096 } => PythonOSError.TypeName(exc.HResult),
        _ => exc.GetType().Name,
    };

    /// <summary>
    /// The approved <c>json-decode-text</c> divergence: <see cref="PythonJson"/> reports a failed decode as <c>invalid JSON document</c> where
    /// Python writes the <c>JSONDecodeError</c> reason and position, after the same <c>Failed to parse ...: </c> prefix.
    /// </summary>
    public static string JsonDecodeText(string text) => DecodeReason().Replace(text, "${prefix}: invalid JSON document");

    /// <summary>A double from the oracle's <c>repr()</c> text.</summary>
    public static double Float(string repr) => repr switch
    {
        "nan" => double.NaN,
        "inf" => double.PositiveInfinity,
        "-inf" => double.NegativeInfinity,
        _ => double.Parse(repr, NumberStyles.Float, CultureInfo.InvariantCulture),
    };

    /// <summary>A long option, as argparse's <c>int</c> holds it.</summary>
    public static long Long(object? value) => (long)PythonBuiltins.Int(value);

    [GeneratedRegex(@"(?<prefix>Failed to parse (?:registry scan|snapshot) [^\n]*?): [^\n:]+: line \d+ column \d+ \(char \d+\)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DecodeReason();
}
