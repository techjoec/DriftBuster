using System.Globalization;
using System.Runtime.InteropServices;

namespace DriftBuster.Backend.Diff;

/// <summary><c>difflib.Match(a, b, size)</c>: <c>a[A:A+Size] == b[B:B+Size]</c>.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct MatchingBlock(int A, int B, int Size)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{A} {B} {Size}");
}
