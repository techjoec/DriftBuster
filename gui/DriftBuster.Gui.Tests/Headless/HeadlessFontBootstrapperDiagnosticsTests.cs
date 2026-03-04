using DriftBuster.Gui.Headless;

namespace DriftBuster.Gui.Tests.Headless;

public sealed class HeadlessFontBootstrapperDiagnosticsTests
{
    [Fact]
    public void RecordSnapshot_overwrites_current_snapshot()
    {
        var first = new HeadlessFontBootstrapperDiagnostics.ProbeSnapshot(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            new[]
            {
                new HeadlessFontBootstrapperDiagnostics.ProbeResult("fonts:SystemFonts", true, "Inter", null),
            },
            ResourceContainsSystemFonts: true,
            ResourceContainsInter: true,
            ResourceCount: 2,
            FailureNotes: null);
        var second = new HeadlessFontBootstrapperDiagnostics.ProbeSnapshot(
            DateTimeOffset.UtcNow,
            new[]
            {
                new HeadlessFontBootstrapperDiagnostics.ProbeResult("Inter", false, null, "missing"),
            },
            ResourceContainsSystemFonts: false,
            ResourceContainsInter: false,
            ResourceCount: 0,
            FailureNotes: "failed");

        HeadlessFontBootstrapperDiagnostics.RecordSnapshot(first);
        HeadlessFontBootstrapperDiagnostics.GetSnapshot().Should().Be(first);

        HeadlessFontBootstrapperDiagnostics.RecordSnapshot(second);
        HeadlessFontBootstrapperDiagnostics.GetSnapshot().Should().Be(second);
    }

    [Fact]
    public void ProbeResult_ToDictionary_includes_expected_keys()
    {
        var result = new HeadlessFontBootstrapperDiagnostics.ProbeResult("fonts:SystemFonts#Inter", false, null, "missing");

        var dictionary = result.ToDictionary();

        dictionary["alias"].Should().Be("fonts:SystemFonts#Inter");
        dictionary["success"].Should().Be("false");
        dictionary["glyph_family"].Should().BeNull();
        dictionary["error"].Should().Be("missing");
    }

    [Fact]
    public void Empty_snapshot_has_known_defaults()
    {
        var snapshot = HeadlessFontBootstrapperDiagnostics.ProbeSnapshot.Empty;

        snapshot.Timestamp.Should().Be(DateTimeOffset.MinValue);
        snapshot.Probes.Should().BeEmpty();
        snapshot.ResourceContainsSystemFonts.Should().BeFalse();
        snapshot.ResourceContainsInter.Should().BeFalse();
        snapshot.ResourceCount.Should().Be(0);
        snapshot.FailureNotes.Should().BeNull();
    }
}
