using System.Runtime.InteropServices;

namespace DriftBuster.Backend.Diff;

/// <summary>
/// One change region of a line diff: the half-open range <c>[BeforeStart, BeforeEnd)</c> of the before lines is replaced by
/// <c>[AfterStart, AfterEnd)</c> of the after lines. Either range may be empty, never both.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct LineChange(int BeforeStart, int BeforeEnd, int AfterStart, int AfterEnd)
{
    /// <summary>Lines removed from the before side.</summary>
    public int Removed => BeforeEnd - BeforeStart;

    /// <summary>Lines inserted from the after side.</summary>
    public int Inserted => AfterEnd - AfterStart;
}
