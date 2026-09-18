namespace DriftBuster.Backend.Curation;

/// <summary>What a curation choice does to its target.</summary>
public static class CurationChoiceKinds
{
    /// <summary>Leave the target out of the differences (a source, a setting, or one value of a setting).</summary>
    public const string Ignore = "ignore";

    /// <summary>Hide the target's values even when they are not detected as secrets.</summary>
    public const string Mask = "mask";

    /// <summary>Show the target's values even when they are detected as secrets.</summary>
    public const string Unmask = "unmask";
}
