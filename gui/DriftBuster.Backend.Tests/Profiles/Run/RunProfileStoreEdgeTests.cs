using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Tests.Infrastructure;
using DriftBuster.Backend.Tests.Secrets;

using static DriftBuster.Backend.Tests.Profiles.Run.RunProfilesTests;

namespace DriftBuster.Backend.Tests.Profiles.Run;

/// <summary>
/// Structured run profile sources (alias, optional and exclude as the offline runner applies them), glob bases, exclusion matching and the
/// GUI definition round trip.
/// </summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class RunProfileStoreEdgeTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SecretRuleCacheIsolation _isolation = new();
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-run-profile-edge-");

    public void Dispose()
    {
        _isolation.Dispose();
        _tmp.Delete(recursive: true);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_tmp.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8);
        return path;
    }

    [Fact]
    public void StructuredSourcesHonourAliasOptionalAndExclude()
    {
        var logs = Path.GetDirectoryName(Write("logs/app.log", "log"))!;
        Write("logs/debug.tmp", "tmp");
        Write("logs/nested/skip.log", "skip");
        Write("logs/nested/keep.log", "keep");
        var single = Write("single.tmp", "single");
        var profile = new RunProfile(
            "structured",
            sources:
            [
                new RunProfileSource(logs) { Alias = "My Logs", Exclude = ["*.tmp", "nested/skip.*"] },
                new RunProfileSource(single) { Exclude = ["*.tmp"] },
                new RunProfileSource(Path.Combine(_tmp.FullName, "absent.txt")) { Optional = true, Alias = "absent" },
                new RunProfileSource(Path.Combine(_tmp.FullName, "gone", "*.txt")) { Optional = true },
            ]);

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, timestamp: "run", cancellationToken: TestContext.Current.CancellationToken);

        result.Files.Select(file => PathText.ToPosix(Path.GetRelativePath(result.OutputDir, file.Destination))).Order(StringComparer.Ordinal)
            .Should().Equal("My-Logs/app.log", "My-Logs/nested/keep.log");
        Directory.Exists(Path.Combine(result.OutputDir, "source_01")).Should().BeTrue();
        Directory.Exists(Path.Combine(result.OutputDir, "absent")).Should().BeTrue();
        Directory.Exists(Path.Combine(result.OutputDir, "source_03")).Should().BeTrue();

        var sources = (List<object?>)RunProfileStore.LoadProfile("structured", baseDir: _tmp.FullName).ToDict()["sources"]!;
        Canonicaliser.DumpsSorted(sources[0], ensureAscii: true).Should().Be(
            "{\n  \"alias\": \"My Logs\",\n  \"exclude\": [\n    \"*.tmp\",\n    \"nested/skip.*\"\n  ],\n  \"path\": " + Canonicaliser.DumpsSorted(logs, ensureAscii: true) + "\n}");
        Canonicaliser.DumpsSorted(sources[2], ensureAscii: true).Should().Be(
            "{\n  \"alias\": \"absent\",\n  \"optional\": true,\n  \"path\": " + Canonicaliser.DumpsSorted(Path.Combine(_tmp.FullName, "absent.txt"), ensureAscii: true) + "\n}");
    }

    [Fact]
    public void ARequiredGlobThatMatchesNothingCopiesNothing()
    {
        Directory.CreateDirectory(Path.Combine(_tmp.FullName, "empty"));
        var pattern = Path.Combine(_tmp.FullName, "empty", "*.txt");
        var profile = new RunProfile("empty-glob", sources: Sources(pattern));

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, cancellationToken: TestContext.Current.CancellationToken);

        result.Files.Should().BeEmpty();
        ((string)ReadMetadata(result)["baseline"]!).Should().Be(pattern);
    }

    [Fact]
    public void AnOptionalBaselineMayBeMissingInAStructuredProfile()
    {
        var missing = Path.Combine(_tmp.FullName, "missing.txt");
        var profile = new RunProfile("optional-baseline", sources: [new RunProfileSource(missing) { Optional = true }], baseline: missing);

        var act = () => RunProfileStore.ValidateProfile(profile);
        act.Should().NotThrow();

        var stringProfile = new RunProfile("string-baseline", sources: Sources(missing), baseline: missing);
        var required = () => RunProfileStore.ValidateProfile(stringProfile);
        required.Should().Throw<FileNotFoundException>().WithMessage("Path does not exist: *");
    }

    [Fact]
    public void DefinitionRoundTripKeepsStructuredSources()
    {
        var definition = new RunProfileDefinition
        {
            Name = "gui",
            Sources = [new RunProfileSource("/a"), new RunProfileSource("/b") { Alias = "b", Optional = true, Exclude = ["*.tmp"] }],
            Baseline = "/a",
            Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" },
            SecretScanner = new SecretScannerOptions { IgnoreRules = [" r "], IgnorePatterns = [] },
        };

        var profile = RunProfile.FromDefinition(definition);
        Canonicaliser.DumpsSorted(profile.ToDict()["secret_scanner"]).Should().Be("{\n  \"ignore_rules\": [\n    \"r\"\n  ]\n}");

        var back = profile.ToDefinition();
        back.Sources.Select(source => source.Path).Should().Equal("/a", "/b");
        back.Sources[1].Alias.Should().Be("b");
        back.Sources[1].Optional.Should().BeTrue();
        back.Sources[1].Exclude.Should().Equal("*.tmp");
        back.Options.Should().Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" });
        back.SecretScanner.IgnoreRules.Should().Equal("r");
        back.SecretScanner.IgnorePatterns.Should().BeEmpty();
    }

    [Fact]
    public void ShouldExcludeMatchesTheRelativePathOrTheName()
    {
        RunProfileExecutor.ShouldExclude("a/b.tmp", ["*.tmp"]).Should().BeTrue();
        RunProfileExecutor.ShouldExclude("a/b.tmp", ["a/*"]).Should().BeTrue();
        RunProfileExecutor.ShouldExclude("a/b.tmp", ["b/*"]).Should().BeFalse();
        RunProfileExecutor.ShouldExclude("a/b.tmp", []).Should().BeFalse();
        RunProfileExecutor.ShouldExclude("B.TMP", ["*.tmp"]).Should().Be(OperatingSystem.IsWindows());
    }
}
