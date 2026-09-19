namespace DriftBuster.Backend.Registry;

/// <summary>The limits every registry tree read keeps to.</summary>
public static class RegistryTreeLimits
{
    /// <summary>Levels below a root that are read.</summary>
    public const int MaxDepth = 12;

    /// <summary>The most keys one read returns.</summary>
    public const int MaxKeys = 20000;

    /// <summary>Seconds one read may take.</summary>
    public const double BudgetSeconds = 60;
}
