namespace DriftBuster.Backend.MultiServer;

public static class BaselineSelector
{
    /// <summary>The baseline host: a preferred host first, then the highest priority, then the earliest plan.</summary>
    public static MultiServerPlan Select(IReadOnlyList<MultiServerPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (plans.Count == 0)
        {
            throw new ArgumentException("At least one plan is required.", nameof(plans));
        }

        var best = plans[0];
        for (var index = 1; index < plans.Count; index++)
        {
            var candidate = plans[index];
            if (candidate.IsPreferred != best.IsPreferred ? candidate.IsPreferred : candidate.Priority > best.Priority)
            {
                best = candidate;
            }
        }

        return best;
    }
}
