using System.Security.Cryptography;
using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <see cref="PythonSort{T}"/> against CPython 3.13's <c>list.sort()</c>: every expected permutation is the index order
/// the interpreter produced sorting wrappers whose <c>__lt__</c> compares the same floats.
/// </summary>
public sealed class PythonSortTests
{
    private static string Permutation(double[] values)
    {
        var indices = Enumerable.Range(0, values.Length).ToList();
        PythonSort<int>.Sort(indices, (left, right) => values[left] < values[right]);
        return string.Join(",", indices);
    }

    private static double[] Values(params double[] values) => values;

    [Fact]
    public void ConsistentOrdersSortStably()
    {
        var items = new List<(int Key, int Order)> { (3, 0), (1, 1), (3, 2), (2, 3), (1, 4), (0, 5) };
        PythonSort<(int Key, int Order)>.Sort(items, (left, right) => left.Key < right.Key);
        items.Should().Equal((0, 5), (1, 1), (1, 4), (2, 3), (3, 0), (3, 2));

        var empty = new List<int>();
        PythonSort<int>.Sort(empty, (left, right) => left < right);
        empty.Should().BeEmpty();
    }

    [Fact]
    public void NaNStaysWhereTheInterpreterLeavesIt()
    {
        Permutation(Values(1.0, double.NaN)).Should().Be("0,1");
        Permutation(Values(double.NaN, 1.0)).Should().Be("0,1");
        Permutation(Values(3.0, double.NaN, 1.0, 2.0)).Should().Be("0,1,2,3");
        Permutation(Values(2.0, 1.0, double.NaN, 0.5, 3.0)).Should().Be("1,2,3,0,4");
    }

    // Lists long enough for natural runs, powersort merges and galloping, with a NaN at every nanEvery-th index.
    [Theory]
    [InlineData(40, 17, 5, "5efc38db7b6a203a50c86dc9c658b03d31dbf20e8776ad427d322d83223cf716")]
    [InlineData(300, 101, 13, "a3c8c9269af6621dee3fc36832b62459cc945bcf3614958686f53ecb13f66aa8")]
    [InlineData(2048, 50, 7, "302871963abd5ed56936cf566115d891fecaf55e62562197fc1f4d4adf407cf9")]
    [InlineData(5000, 997, 31, "38c4aa3d578600aa330adb73ef3bd6ca5525704cb15d75a66844a3e6c3899535")]
    public void LongListsWithNaNMatchTheInterpreter(int count, int modulus, int nanEvery, string sha256)
    {
        var values = Enumerable.Range(0, count).Select(index => index % nanEvery == 0 ? double.NaN : (double)(index * 7919 % modulus)).ToArray();

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(Permutation(values))));

        digest.Should().Be(sha256);
    }

    [Fact]
    public void AThrowingComparisonPropagates()
    {
        var items = new List<int> { 2, 1 };
        var act = () => PythonSort<int>.Sort(items, (_, _) => throw new InvalidOperationException("no order"));
        act.Should().Throw<InvalidOperationException>().WithMessage("no order");
    }
}
