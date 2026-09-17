using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Tests.Profiles.Detection;

/// <summary>Profiles suppressing review flags: the real YAML plugin flags the tab-indented file.</summary>
public sealed class DetectorProfileReviewIgnoreTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-review-ignore-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void ScanWithProfilesReviewIgnore()
    {
        File.WriteAllText(Path.Combine(_tmp.FullName, "config.yaml"), "apiVersion: v1\n\tkind: ConfigMap\n", new UTF8Encoding(false));

        var profile = new DetectionProfile(
            "default",
            configs:
            [
                new DetectionProfileConfig(
                    "cfg1",
                    path: "config.yaml",
                    metadata: new Dictionary<string, object?>(StringComparer.Ordinal) { ["ignore_review_flags"] = true }),
            ]);
        var store = new DetectionProfileStore([profile]);

        var detector = new Detector();
        var results = detector.ScanWithProfiles(_tmp.FullName, store);
        results.Should().NotBeEmpty();
        results[0].Detection.Should().NotBeNull();
        var md = results[0].Detection!.Metadata ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        md.GetValueOrDefault("review_ignored").Should().Be(true);
        md.GetValueOrDefault("needs_review").Should().Be(false);
    }
}
