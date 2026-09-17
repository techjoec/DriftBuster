using System.Numerics;
using System.Text.RegularExpressions;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// <c>registry.scan.SearchSpec</c>: keywords every one of which must appear in <c>"{name} {text}"</c> (lower-cased), patterns one of
/// which must find the text or the name, the deepest subkey level walked below a root, the hit limit and the time budget in seconds.
/// The two limits are Python <c>int</c>s of any size (<c>argparse</c> and <c>int()</c> take any), clamped by the search itself.
/// </summary>
public sealed record SearchSpec
{
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public IReadOnlyList<Regex> Patterns { get; init; } = [];

    public BigInteger MaxDepth { get; init; } = 12;

    public BigInteger MaxHits { get; init; } = 200;

    public double TimeBudgetS { get; init; } = 10.0;
}
