namespace DriftBuster.Backend.MultiServer;

/// <summary><c>MultiServerRunner._select_baseline</c>.</summary>
public static class BaselineSelector
{
    /// <summary>
    /// The first plan of <c>sorted(plans, key=(not is_preferred, -priority, index))</c>: a preferred host before any other, then
    /// the highest priority, then the earliest plan.
    /// </summary>
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
