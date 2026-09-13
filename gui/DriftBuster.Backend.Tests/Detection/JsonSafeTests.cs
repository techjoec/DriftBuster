using System.Globalization;
using System.Numerics;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Detection;

/// <summary>
/// <see cref="DetectionMetadata.JsonSafe"/> against Python's <c>_json_safe</c>; every expected string is the
/// interpreter's own <c>str()</c> of the equivalent value.
/// </summary>
public sealed class JsonSafeTests
{
    [Fact]
    public void IntegersOfAnySizePassThroughAsNumbers()
    {
        var big = BigInteger.Parse("1267650600228229401496703205376", CultureInfo.InvariantCulture);
        var converted = DetectionMetadata.JsonSafe(new List<object?> { new BigInteger(-5), big, true, 1.5, new BigInteger(2) });

        converted.Should().BeOfType<List<object?>>().Which.Should().Equal(new BigInteger(-5), big, true, 1.5, new BigInteger(2));
    }

    [Theory]
    [InlineData(2001, 1, 1, 0, 0, 1, 500000, "2001-01-01 00:00:01.500000")]
    [InlineData(1, 1, 1, 0, 0, 0, 0, "0001-01-01 00:00:00")]
    [InlineData(9999, 12, 31, 23, 59, 59, 999999, "9999-12-31 23:59:59.999999")]
    [InlineData(2020, 2, 29, 13, 5, 7, 1, "2020-02-29 13:05:07.000001")]
    public void DatesUsePythonDatetimeStr(int year, int month, int day, int hour, int minute, int second, int microsecond, string expected)
    {
        var date = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified).AddTicks(microsecond * 10L);

        DetectionMetadata.JsonSafe(date).Should().Be(expected);
        DetectionMetadata.JsonSafe(new List<object?> { date }).Should().BeOfType<List<object?>>().Which.Should().Equal(expected);
    }

    [Fact]
    public void PlistUidsUseTheirRepr()
    {
        DetectionMetadata.JsonSafe(new BinaryPlist.Uid(7)).Should().Be("UID(7)");
        DetectionMetadata.JsonSafe(new BinaryPlist.Uid(BigInteger.One << 63)).Should().Be("UID(9223372036854775808)");
    }

    [Fact]
    public void DictionaryKeysUsePythonStr()
    {
        var source = new Dictionary<object, object?> { [1.0] = "a", [true] = "c", [BigInteger.Pow(10, 30)] = "d" };

        var converted = DetectionMetadata.JsonSafe(source).Should().BeOfType<OrderedDictionary<string, object?>>().Subject;

        converted.Keys.Should().Equal("1.0", "True", "1000000000000000000000000000000");
        DetectionMetadata.PythonStr(null).Should().Be("None");
        DetectionMetadata.PythonStr(false).Should().Be("False");
        DetectionMetadata.PythonStr(1e16).Should().Be("1e+16");
        DetectionMetadata.PythonStr(0.5f).Should().Be("0.5");
    }

    [Fact]
    public void ReadOnlyDictionariesKeepOrderAndRepeatedKeysKeepTheFirstSlot()
    {
        IReadOnlyDictionary<int, object?> readOnly = new SortedList<int, object?> { [2] = "b", [1] = new List<object?> { "x" } };
        var converted = DetectionMetadata.JsonSafe(readOnly).Should().BeOfType<OrderedDictionary<string, object?>>().Subject;
        converted.Keys.Should().Equal("1", "2");
        converted["1"].Should().BeOfType<List<object?>>().Which.Should().Equal("x");

        var repeated = new Dictionary<object, object?> { ["1"] = "first", [1] = "last" };
        var merged = DetectionMetadata.JsonSafe(repeated).Should().BeOfType<OrderedDictionary<string, object?>>().Subject;
        merged.Should().ContainSingle().Which.Value.Should().Be("last");
    }

    [Fact]
    public void DeepNestingIsConvertedWithoutRecursion()
    {
        // A registry-live max_depth may hold the deepest value json.loads returns; converting it must not need the stack.
        var depth = PythonJson.MaxNestingDepth;
        PythonJson.TryLoads(new string('[', depth) + "1" + new string(']', depth), out var deep).Should().BeTrue();
        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["max_depth"] = deep };

        var converted = StackProbe.RunOnSmallStack(() => DetectionMetadata.JsonSafe(metadata));

        var level = ((OrderedDictionary<string, object?>)converted!)["max_depth"];
        for (var index = 0; index < depth; index++)
        {
            level = level.Should().BeOfType<List<object?>>().Which.Should().ContainSingle().Subject;
        }

        level.Should().Be(1);
    }
}
