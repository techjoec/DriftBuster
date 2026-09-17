using System.Collections;
using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Tests.Detection;

/// <summary>
/// Detector review-flag ignore exceptions: the real YAML plugin flags the tab-indented file.
/// </summary>
public sealed class DetectorIgnoreExceptionTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-ignore-");

    public void Dispose() => _tmp.Delete(recursive: true);

    // The applied configs blow up as soon as the ignore-review check enumerates them.
    private sealed class RaisingConfigs : IReadOnlyList<AppliedProfileConfig>
    {
        public AppliedProfileConfig this[int index] => throw new InvalidOperationException("boom");

        public int Count => 1;

        public IEnumerator<AppliedProfileConfig> GetEnumerator() => throw new InvalidOperationException("boom");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class StoreStub : IProfileMatcher
    {
        public IReadOnlyList<AppliedProfileConfig> MatchingConfigs(IReadOnlySet<string> tags, string? relativePath) => new RaisingConfigs();
    }

    [Fact]
    public void ScanWithProfilesIgnoreException()
    {
        var target = Path.Combine(_tmp.FullName, "config.yaml");
        File.WriteAllText(target, "apiVersion: v1\n\tkind: ConfigMap\n", new UTF8Encoding(false));

        var detector = new Detector();
        var results = detector.ScanWithProfiles(_tmp.FullName, new StoreStub());

        results.Should().NotBeEmpty();
        results[0].Detection.Should().NotBeNull();
        results[0].Detection!.Variant.Should().Be("kubernetes-manifest");
        // needs_review stays set: the exception makes ignore false.
        results[0].Detection!.Metadata!["needs_review"].Should().Be(true);
        results[0].Detection!.Metadata!["review_reasons"].Should().BeAssignableTo<System.Collections.IEnumerable>().Subject
            .Cast<object?>().Should().Equal("Tab indentation present in YAML-like content");
        results[0].Detection!.Metadata.Should().NotContainKey("review_ignored");
        results[0].Profiles.Should().BeOfType<RaisingConfigs>();
    }
}
