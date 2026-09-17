namespace DriftBuster.Backend.Tests.Diff;

/// <summary>
/// Test classes that build diffs share the process-wide <c>DiffSafetyLimits</c> threshold seams, which one test swaps; the collection
/// keeps them from running concurrently.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DiffSafetyLimitsCollection
{
    public const string Name = "diff-safety-limits";
}
