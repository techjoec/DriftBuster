using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Tests.Secrets;

namespace DriftBuster.Backend.Tests.Profiles.Run;

/// <summary>
/// Run profile saving, loading, validation and execution. Execution loads the packaged secret rules, so the class shares the rule cache
/// collection.
/// </summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class RunProfilesTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SecretRuleCacheIsolation _isolation = new();
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-run-profiles-");

    public void Dispose()
    {
        _isolation.Dispose();
        _tmp.Delete(recursive: true);
    }

    internal static RunProfileSource[] Sources(params string[] paths) => paths.Select(path => new RunProfileSource(path)).ToArray();

    internal static OrderedDictionary<string, object?> Map(params (string Key, object? Value)[] items)
    {
        var map = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            map[key] = value;
        }

        return map;
    }

    internal static OrderedDictionary<string, object?> ReadMetadata(ProfileRunResult result)
    {
        EngineJson.TryLoads(File.ReadAllText(Path.Combine(result.OutputDir, "metadata.json"), Encoding.UTF8), out var metadata).Should().BeTrue();
        return metadata.Should().BeOfType<OrderedDictionary<string, object?>>().Subject;
    }

    private string Tmp(params string[] parts) => Path.Combine([_tmp.FullName, .. parts]);

    [Fact]
    public void SaveAndLoadProfile()
    {
        var profile = new RunProfile(
            "vdi",
            description: "VDI configuration set",
            sources: Sources("*.json"),
            options: Map(("sample_size", 65536)),
            secretScanner: Map(("ignore_rules", new List<object?> { "db" }), ("ignore_patterns", new List<object?> { "ALLOW" })));

        RunProfileStore.SaveProfile(profile, baseDir: _tmp.FullName);

        var loaded = RunProfileStore.LoadProfile("vdi", baseDir: _tmp.FullName);
        loaded.Name.Should().Be("vdi");
        loaded.Sources.Select(source => source.Path).Should().Equal("*.json");
        loaded.Options["sample_size"].Should().Be("65536");
        ((List<object?>)loaded.SecretScanner.GetValueOrDefault("ignore_rules", new List<object?>())!).Should().Equal("db");
        ((List<object?>)loaded.SecretScanner.GetValueOrDefault("ignore_patterns", new List<object?>())!).Should().Equal("ALLOW");
    }

    [Fact]
    public void ExecuteProfileCollectsFiles()
    {
        var profileDir = Directory.CreateDirectory(Tmp("configs"));
        File.WriteAllText(Path.Combine(profileDir.FullName, "app.json"), "{\"key\": 1}", Utf8);
        File.WriteAllText(Path.Combine(profileDir.FullName, "web.config"), "<configuration />", Utf8);

        var profile = new RunProfile("demo", sources: Sources(profileDir.FullName), options: Map());

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, cancellationToken: TestContext.Current.CancellationToken);

        Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(result.OutputDir))).Should().Be("demo");
        var copiedFiles = EngineGlob.Glob(result.OutputDir, "**/*", TestContext.Current.CancellationToken);
        copiedFiles.Should().Contain(path => PathText.Name(path) == "app.json");
        var metadata = ReadMetadata(result);
        ((OrderedDictionary<string, object?>)metadata["profile"]!)["name"].Should().Be("demo");
        ((List<object?>)metadata["files"]!).Should().HaveCount(2);
        var secrets = (OrderedDictionary<string, object?>)metadata["secrets"]!;
        ((List<object?>)secrets["findings"]!).Should().BeEmpty();
        secrets.Should().ContainKey("ruleset_version");
        var snapshot = result.ToDict();
        ((OrderedDictionary<string, object?>)snapshot["profile"]!)["name"].Should().Be("demo");
        ((List<object?>)((OrderedDictionary<string, object?>)snapshot["secrets"]!)["findings"]!).Should().BeEmpty();
    }

    [Fact]
    public void ExecuteProfileRespectsBaselineOrder()
    {
        var fileA = Tmp("a.json");
        var fileB = Tmp("b.json");
        File.WriteAllText(fileA, "{}", Utf8);
        File.WriteAllText(fileB, "{}", Utf8);

        var profile = new RunProfile("order", sources: Sources(fileA, fileB), baseline: fileB);

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, cancellationToken: TestContext.Current.CancellationToken);
        var metadata = ReadMetadata(result);
        ((string)metadata["baseline"]!).Should().EndWith("b.json");
    }

    [Fact]
    public void ExecuteProfileRejectsMissingSource()
    {
        var profile = new RunProfile("missing", sources: Sources(Tmp("missing.json")));

        var act = () => RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, cancellationToken: TestContext.Current.CancellationToken);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ExecuteProfileRejectsMissingGlobBase()
    {
        var profile = new RunProfile("globby", sources: Sources(Tmp("ghost", "*.json")));

        var act = () => RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, cancellationToken: TestContext.Current.CancellationToken);
        act.Should().Throw<FileNotFoundException>().WithMessage("*Glob base directory not found*");
    }

    [Fact]
    public void ExecuteProfileRequiresBaselineInSources()
    {
        var source = Tmp("data.json");
        File.WriteAllText(source, "{}", Utf8);

        var profile = new RunProfile("baseline", sources: Sources(source), baseline: Tmp("other.json"));

        var act = () => RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, cancellationToken: TestContext.Current.CancellationToken);
        act.Should().Throw<EngineValueException>();
    }

    [Fact]
    public void LoadProfileMissing()
    {
        var act = () => RunProfileStore.LoadProfile("absent", baseDir: _tmp.FullName);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ExecuteProfileBaselineGlobWithExistingBase()
    {
        var baseDir = Directory.CreateDirectory(Tmp("glob"));
        File.WriteAllText(Path.Combine(baseDir.FullName, "one.txt"), "1", Utf8);

        var baselineGlob = Path.Combine(baseDir.FullName, "*.txt");
        var profile = new RunProfile("glob-existing", sources: Sources(baselineGlob), baseline: baselineGlob);

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, cancellationToken: TestContext.Current.CancellationToken);
        result.Files.Should().Contain(file => file.Source == baselineGlob);
    }

    [Fact]
    public void CopyFileHandlesNonRelativePaths()
    {
        var source = Tmp("outside.txt");
        File.WriteAllText(source, "content", Utf8);
        var destinationRoot = Directory.CreateDirectory(Tmp("dest"));

        var profileFile = RunProfileExecutor.CopyFile(
            source: source,
            file: source,
            basePath: Path.GetDirectoryName(Path.GetDirectoryName(source))!,
            destinationRoot: destinationRoot.FullName,
            cancellationToken: TestContext.Current.CancellationToken);

        File.Exists(profileFile.Destination).Should().BeTrue();
        PathText.Name(profileFile.Destination).Should().Be(PathText.Name(source));
    }

    [Fact]
    public void CollectMatchesReturnsGlobResults()
    {
        var match = Directory.CreateDirectory(Tmp("data"));
        File.WriteAllText(Path.Combine(match.FullName, "one.txt"), "1", Utf8);
        File.WriteAllText(Path.Combine(match.FullName, "two.txt"), "2", Utf8);

        var results = RunProfileExecutor.CollectMatches(Path.Combine(match.FullName, "*.txt"), TestContext.Current.CancellationToken);
        results.Should().HaveCount(2);
    }

    [Fact]
    public void ValidateProfileBaselineMissingPath()
    {
        var missing = Tmp("nope.txt");
        var profile = new RunProfile("base", sources: Sources(missing), baseline: missing);

        var act = () => RunProfileStore.ValidateProfile(profile);
        act.Should().Throw<FileNotFoundException>();
    }
}
