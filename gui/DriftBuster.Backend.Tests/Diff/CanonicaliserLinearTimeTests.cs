using System.Diagnostics;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Tests.Infrastructure;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>
/// <see cref="Canonicaliser.CanonicaliseXml"/> builds text of many character references and processing instructions in linear time.
/// Linearity is asserted by comparing the fastest of several timings of an input with that of an input four times its size in the same
/// run: a linear build takes about four times as long, a quadratic one sixteen. The class runs outside the parallel collections.
/// </summary>
[Collection(WallClockCollection.Name)]
public sealed class CanonicaliserLinearTimeTests
{
    private const int Small = 25000;
    private const int Runs = 3;

    // Generous: four is linear, sixteen quadratic; the fastest of several runs absorbs most scheduling noise from a loaded host.
    private const double MaxRatio = 9.0;

    [Fact]
    public void XmlTextOfManyReferencesAndInstructionsIsBuiltInLinearTime()
    {
        _ = Canonicalise(Small);
        var small = Fastest(Small);
        var large = Fastest(4 * Small);
        (large.TotalMilliseconds / Math.Max(small.TotalMilliseconds, 1.0)).Should().BeLessThan(
            MaxRatio, "{0} for {1} units against {2} for {3}", large, 4 * Small, small, Small);
    }

    private static TimeSpan Fastest(int count)
    {
        var fastest = TimeSpan.MaxValue;
        for (var run = 0; run < Runs; run++)
        {
            var watch = Stopwatch.StartNew();
            var canonical = Canonicalise(count);
            watch.Stop();
            canonical.Should().Be("<a>" + string.Concat(Enumerable.Repeat("x&amp;", count)) + "\n  <b />" + new string('A', count) + "\n</a>");
            fastest = watch.Elapsed < fastest ? watch.Elapsed : fastest;
        }

        return fastest;
    }

    private static string Canonicalise(int count)
        => Canonicaliser.CanonicaliseXml(
            "<a>" + string.Concat(Enumerable.Repeat("x&amp;<?p?>", count)) + "<b/>" + string.Concat(Enumerable.Repeat("&#65;", count)) + "</a>");
}
