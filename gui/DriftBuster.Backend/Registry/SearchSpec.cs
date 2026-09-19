using System.Numerics;
using System.Text.RegularExpressions;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// Keywords that must all occur in the lower-cased "{name} {text}", patterns one of which must find the text or the name, the
/// deepest subkey level, the hit limit and the time budget in seconds. The limits are arbitrary-size integers the search clamps.
/// </summary>
public sealed record SearchSpec
{
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public IReadOnlyList<Regex> Patterns { get; init; } = [];

    public BigInteger MaxDepth { get; init; } = 12;

    public BigInteger MaxHits { get; init; } = 200;

    public double TimeBudgetS { get; init; } = 10.0;
}
