using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests;

namespace DriftBuster.Gui.Tests.Services;

[Collection(EnvironmentVariablesCollection.Name)]
public sealed class PerformanceProfileTests : IDisposable
{
    private readonly string? _origThreshold;
    private readonly string? _origForce;

    public PerformanceProfileTests()
    {
        _origThreshold = Environment.GetEnvironmentVariable("DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD");
        _origForce = Environment.GetEnvironmentVariable("DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD", _origThreshold);
        Environment.SetEnvironmentVariable("DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION", _origForce);
    }

    [Fact]
    public void Default_threshold_is_400()
    {
        var profile = new PerformanceProfile();
        profile.VirtualizationThreshold.Should().Be(400);
        profile.ForceVirtualizationOverride.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_rejects_non_positive_threshold(int threshold)
    {
        Action act = () => new PerformanceProfile(threshold);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(399, false)]
    [InlineData(400, true)]
    [InlineData(401, true)]
    public void ShouldVirtualize_compares_against_threshold(int itemCount, bool expected)
    {
        var profile = new PerformanceProfile();
        profile.ShouldVirtualize(itemCount).Should().Be(expected);
    }

    [Fact]
    public void ShouldVirtualize_rejects_negative_count()
    {
        var profile = new PerformanceProfile();
        Action act = () => profile.ShouldVirtualize(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1000, true)]
    [InlineData(false, 0, false)]
    [InlineData(false, 1000, false)]
    public void ForceOverride_takes_precedence_over_threshold(bool force, int itemCount, bool expected)
    {
        var profile = new PerformanceProfile(forceVirtualizationOverride: force);
        profile.ShouldVirtualize(itemCount).Should().Be(expected);
    }

    [Fact]
    public void WithThreshold_returns_new_profile_preserving_force()
    {
        var original = new PerformanceProfile(200, true);
        var updated = original.WithThreshold(500);
        updated.VirtualizationThreshold.Should().Be(500);
        updated.ForceVirtualizationOverride.Should().Be(true);
    }

    [Fact]
    public void WithForceOverride_returns_new_profile_preserving_threshold()
    {
        var original = new PerformanceProfile(200);
        var updated = original.WithForceOverride(false);
        updated.VirtualizationThreshold.Should().Be(200);
        updated.ForceVirtualizationOverride.Should().Be(false);
    }

    [Theory]
    [InlineData("100", null, 100, null)]
    [InlineData(null, "true", 400, true)]
    [InlineData(null, "1", 400, true)]
    [InlineData(null, "yes", 400, true)]
    [InlineData(null, "on", 400, true)]
    [InlineData(null, "false", 400, false)]
    [InlineData(null, "0", 400, false)]
    [InlineData(null, "no", 400, false)]
    [InlineData(null, "off", 400, false)]
    [InlineData("abc", null, 400, null)]
    [InlineData("-5", null, 400, null)]
    [InlineData("0", null, 400, null)]
    [InlineData(null, "maybe", 400, null)]
    [InlineData("", "", 400, null)]
    public void FromEnvironment_parses_variables(string? threshold, string? force, int expectedThreshold, bool? expectedForce)
    {
        Environment.SetEnvironmentVariable("DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD", threshold);
        Environment.SetEnvironmentVariable("DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION", force);

        var profile = PerformanceProfile.FromEnvironment();

        profile.VirtualizationThreshold.Should().Be(expectedThreshold);
        profile.ForceVirtualizationOverride.Should().Be(expectedForce);
    }
}
