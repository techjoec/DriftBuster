namespace DriftBuster.Gui.ViewModels
{
    /// <summary>What a diff line is: shared, changed on both sides, only on one side, a fold of unchanged lines, or a hunk header.</summary>
    public enum DiffLineKind
    {
        Same,
        Changed,
        Removed,
        Added,
        Folded,
        Header,
    }
}
