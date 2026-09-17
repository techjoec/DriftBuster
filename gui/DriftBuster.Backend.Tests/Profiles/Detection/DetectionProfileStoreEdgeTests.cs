using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Tests.Profiles.Detection;

/// <summary>
/// Detection profile store edges: renaming a profile, version and branch tags with globs across separators, and a profile-aware scan
/// through a nested directory.
/// </summary>
public sealed class DetectionProfileStoreEdgeTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-profile-edges-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void RenamedProfileIsReindexedUnderItsNewName()
    {
        var store = new DetectionProfileStore([new DetectionProfile("a", configs: [new DetectionProfileConfig("1")])]);

        var renamed = store.UpdateProfile("a", profile => profile with { Name = "z", Description = "moved" });

        store.Profiles().Should().Equal(renamed);
        store.FindConfig("1").Should().ContainSingle().Which.Profile.Should().BeSameAs(renamed);
        var getOld = () => store.GetProfile("a");
        getOld.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void MatchingHonoursVersionAndBranchTagsAndGlobsAcrossSeparators()
    {
        var config = new DetectionProfileConfig("c", pathGlob: "configs/*.json", version: "2", branch: "main");
        var tags = new HashSet<string>(["version:2", "branch:main"], StringComparer.Ordinal);

        config.Matches("configs/sub/app.json", tags).Should().BeTrue();
        config.Matches("./configs//app.json", tags).Should().BeTrue();
        config.Matches("configs/app.json", new HashSet<string>(["version:2"], StringComparer.Ordinal)).Should().BeFalse();
        config.Matches(null, tags).Should().BeFalse();

        var exact = new DetectionProfileConfig("e", path: "configs/app.json", pathGlob: "*.yaml");
        exact.Matches("configs/app.json", tags).Should().BeTrue();
        exact.Matches("x.yaml", tags).Should().BeTrue();
        exact.Matches("x.json", tags).Should().BeFalse();
    }

    [Fact]
    public void ScanWithProfilesMatchesNestedPathsRelativeToTheRoot()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "configs", "sub")).FullName;
        File.WriteAllText(Path.Combine(nested, "app.json"), "{\"a\": 1}", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_tmp.FullName, "top.json"), "{\"b\": 2}", new UTF8Encoding(false));
        var store = new DetectionProfileStore(
        [
            new DetectionProfile(
                "nested",
                configs:
                [
                    new DetectionProfileConfig("glob", pathGlob: "configs/*.json"),
                    new DetectionProfileConfig("top", path: "top.json", application: "svc"),
                ]),
        ]);

        var results = new Detector().ScanWithProfiles(_tmp.FullName + "/", store, tags: ["application:svc"]);

        results.Select(result => (PathText.Name(result.Path), string.Join(",", result.Profiles.Select(applied => applied.Config.Identifier))))
            .Should().Equal(("app.json", "glob"), ("top.json", "top"));
        results.Should().OnlyContain(result => result.Detection != null && !result.Detection.Metadata!.ContainsKey("review_ignored"));
    }
}
