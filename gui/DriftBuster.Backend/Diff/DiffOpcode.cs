using System.Globalization;
using System.Runtime.InteropServices;

namespace DriftBuster.Backend.Diff;

/// <summary>
/// One <c>(tag, i1, i2, j1, j2)</c> tuple: <c>a[i1:i2]</c> becomes <c>b[j1:j2]</c>. The bounds are 64-bit because
/// <c>get_grouped_opcodes(n)</c> computes <c>i2 - n</c> and <c>i1 + n</c> with Python integers: for a context past
/// 2^30 in either direction they leave the 32-bit range (and Python slices and prints them as they are).
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct DiffOpcode(DiffOpcodeTag Tag, long I1, long I2, long J1, long J2)
{
    /// <summary>The tag as difflib spells it (<c>equal</c>, <c>replace</c>, <c>delete</c>, <c>insert</c>).</summary>
    public string TagName => Tag switch
    {
        DiffOpcodeTag.Equal => "equal",
        DiffOpcodeTag.Replace => "replace",
        DiffOpcodeTag.Delete => "delete",
        _ => "insert",
    };

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{TagName} {I1} {I2} {J1} {J2}");
}
