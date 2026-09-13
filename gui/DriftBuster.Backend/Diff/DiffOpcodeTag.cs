namespace DriftBuster.Backend.Diff;

/// <summary>The tags <c>difflib.SequenceMatcher.get_opcodes</c> emits.</summary>
public enum DiffOpcodeTag
{
    Equal,
    Replace,
    Delete,
    Insert,
}
