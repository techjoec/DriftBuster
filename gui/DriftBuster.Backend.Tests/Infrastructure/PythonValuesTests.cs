using System.Numerics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary><see cref="PythonValues"/> against CPython 3.13 <c>==</c>, <c>hash()</c>, <c>&lt;</c> and <c>sorted()</c>.</summary>
public sealed class PythonValuesTests
{
    [Fact]
    public void EqualityFollowsPython()
    {
        PythonValues.Equal(1, true).Should().BeTrue();
        PythonValues.Equal(1L, 1.0).Should().BeTrue();
        PythonValues.Equal(new BigInteger(9007199254740993L), 9007199254740992.0).Should().BeFalse();
        PythonValues.Equal(double.NaN, double.NaN).Should().BeFalse();
        object nan = double.NaN;
        PythonValues.Equal(nan, nan).Should().BeTrue();
        PythonValues.Equal(0.5, 0).Should().BeFalse();
        PythonValues.Equal(double.PositiveInfinity, BigInteger.Pow(10, 400)).Should().BeFalse();
        PythonValues.Equal("1", 1).Should().BeFalse();
        PythonValues.Equal(null, null).Should().BeTrue();
        PythonValues.Equal(null, 0).Should().BeFalse();
        PythonValues.Equal(new List<object?> { 1, "a" }, new List<object?> { 1.0, "a" }).Should().BeTrue();
        PythonValues.Equal(new List<object?> { 1 }, new List<object?> { 1, 2 }).Should().BeFalse();
        PythonValues.Equal("ab", new List<object?> { "a", "b" }).Should().BeFalse();
        var left = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["a"] = 1, ["b"] = false };
        var right = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["b"] = 0, ["a"] = 1.0 };
        PythonValues.Equal(left, right).Should().BeTrue();
        right["c"] = null;
        PythonValues.Equal(left, right).Should().BeFalse();
    }

    [Fact]
    public void HashKeysDeduplicateEqualNumbersAndRefuseContainers()
    {
        var set = new HashSet<object?>(PythonValues.HashKeys) { true, 1, 1.0, 2L, "2", null };
        set.Should().HaveCount(4);
        set.Should().Contain(true).And.Contain(2.0).And.Contain((object?)null);

        var list = () => set.Add(new List<object?>());
        list.Should().Throw<PythonTypeException>().WithMessage("unhashable type: 'list'");
        var dict = () => set.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal));
        dict.Should().Throw<PythonTypeException>().WithMessage("unhashable type: 'dict'");
    }

    [Fact]
    public void OrderingFollowsPython()
    {
        PythonValues.Sorted(["\uffff", "\U0001F600", "B", "a"]).Should().Equal("B", "a", "\uffff", "\U0001F600");
        PythonValues.Sorted([2.5, true, BigInteger.Pow(10, 30), -1L, double.NegativeInfinity])
            .Should().Equal(double.NegativeInfinity, -1L, true, 2.5, BigInteger.Pow(10, 30));
        PythonValues.LessThan(1, 1.5).Should().BeTrue();
        PythonValues.LessThan(2.0, 2).Should().BeFalse();
        PythonValues.LessThan(double.NaN, 1).Should().BeFalse();
        PythonValues.LessThan(1, double.NaN).Should().BeFalse();
        PythonValues.LessThan(BigInteger.Pow(10, 400), double.PositiveInfinity).Should().BeTrue();

        var mixed = () => PythonValues.LessThan("a", 1);
        mixed.Should().Throw<PythonTypeException>().WithMessage("'<' not supported between instances of 'str' and 'int'");
        var none = () => PythonValues.LessThan(null, null);
        none.Should().Throw<PythonTypeException>().WithMessage("'<' not supported between instances of 'NoneType' and 'NoneType'");
    }

    [Fact]
    public void LessThanOrEqualFollowsPython()
    {
        PythonValues.LessThanOrEqual(0.5, 0).Should().BeFalse();
        PythonValues.LessThanOrEqual(0, 0).Should().BeTrue();
        PythonValues.LessThanOrEqual(false, 0).Should().BeTrue();
        PythonValues.LessThanOrEqual(-1L, 0).Should().BeTrue();
        PythonValues.LessThanOrEqual(double.NegativeInfinity, 0).Should().BeTrue();
        PythonValues.LessThanOrEqual(double.NaN, 0).Should().BeFalse();
        PythonValues.LessThanOrEqual(0, double.NaN).Should().BeFalse();
        PythonValues.LessThanOrEqual(BigInteger.Pow(10, 30), 1e30).Should().BeTrue();
        PythonValues.LessThanOrEqual("a", "a").Should().BeTrue();
        PythonValues.LessThanOrEqual("b", "a").Should().BeFalse();
        PythonValues.LessThanOrEqual(new List<object?> { 1 }, new List<object?> { 1 }).Should().BeTrue();
        PythonValues.LessThanOrEqual(new object?[] { 1, 2 }, new object?[] { 1 }).Should().BeFalse();

        var text = () => PythonValues.LessThanOrEqual("2", 0);
        text.Should().Throw<PythonTypeException>().WithMessage("'<=' not supported between instances of 'str' and 'int'");
        var list = () => PythonValues.LessThanOrEqual(new List<object?> { 1 }, 0);
        list.Should().Throw<PythonTypeException>().WithMessage("'<=' not supported between instances of 'list' and 'int'");
        var dict = () => PythonValues.LessThanOrEqual(new OrderedDictionary<string, object?>(StringComparer.Ordinal), 0);
        dict.Should().Throw<PythonTypeException>().WithMessage("'<=' not supported between instances of 'dict' and 'int'");
    }

    [Fact]
    public void NarrowPicksTheSmallestIntegerType()
    {
        PythonValues.Narrow(new BigInteger(int.MaxValue)).Should().BeOfType<int>();
        PythonValues.Narrow(new BigInteger(int.MinValue) - 1).Should().BeOfType<long>();
        PythonValues.Narrow(new BigInteger(long.MaxValue) + 1).Should().BeOfType<BigInteger>();
    }

    [Fact]
    public void TuplesCompareHashAndSortAsPython()
    {
        PythonValues.Equal(new object?[] { 1, "a" }, new object?[] { 1.0, "a" }).Should().BeTrue();
        PythonValues.Equal(new object?[] { 1 }, new List<object?> { 1 }).Should().BeFalse();
        PythonValues.LessThan(new object?[] { 1, 2 }, new object?[] { 1, 3 }).Should().BeTrue();
        PythonValues.LessThan(new object?[] { 1 }, new object?[] { 1, 0 }).Should().BeTrue();
        PythonValues.LessThan(new object?[] { 1, 0 }, new object?[] { 1 }).Should().BeFalse();
        PythonValues.LessThan(new List<object?> { 1, 2 }, new List<object?> { 1, 3 }).Should().BeTrue();

        var mixed = () => PythonValues.LessThan(new object?[] { "a", null }, new object?[] { "a", 1 });
        mixed.Should().Throw<PythonTypeException>().WithMessage("'<' not supported between instances of 'NoneType' and 'int'");
        var tupleAndList = () => PythonValues.LessThan(new object?[] { 1 }, new List<object?> { 1 });
        tupleAndList.Should().Throw<PythonTypeException>().WithMessage("'<' not supported between instances of 'tuple' and 'list'");
        var dicts = () => PythonValues.LessThan(new OrderedDictionary<string, object?>(StringComparer.Ordinal), new OrderedDictionary<string, object?>(StringComparer.Ordinal));
        dicts.Should().Throw<PythonTypeException>().WithMessage("'<' not supported between instances of 'dict' and 'dict'");

        var set = new HashSet<object?>(PythonValues.HashKeys) { new object?[] { 1, "a" }, new object?[] { true, "a" }, new object?[] { 1.0, "a", null } };
        set.Should().HaveCount(2);
        var unhashable = () => PythonValues.HashKeys.GetHashCode(new object?[] { 1, new List<object?> { 2 } });
        unhashable.Should().Throw<PythonTypeException>().WithMessage("unhashable type: 'list'");

        PythonValues.Sorted([new object?[] { "b", 1 }, new object?[] { "a", 2 }, new object?[] { "a", 1 }])
            .Select(item => PythonRepr.Repr(item)).Should().Equal("('a', 1)", "('a', 2)", "('b', 1)");
    }
}
