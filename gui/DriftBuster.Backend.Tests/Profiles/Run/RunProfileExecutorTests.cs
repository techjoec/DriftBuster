using System.Text.Json;

using DriftBuster.Backend.Json;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Tests.Secrets;

namespace DriftBuster.Backend.Tests.Profiles.Run;

/// <summary>A profile run: collection per source, skips and refusals, the secret filter, and <c>metadata.json</c>.</summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class RunProfileExecutorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2025, 3, 1, 8, 30, 0, TimeSpan.Zero);
    private readonly SecretRuleCacheIsolation _isolation = new();
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-run-");

    public void Dispose()
    {
        _isolation.Dispose();
        _tmp.Delete(recursive: true);
    }

    private string Data => Path.Join(_tmp.FullName, "data");

    private string Write(string relative, string text = "setting=1\n")
    {
        var path = Path.Join(Data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private RunProfileRunResult Run(params RunProfileSource[] sources)
        => new RunProfileExecutor(_tmp.FullName, new FixedTimeProvider(Now))
            .Execute(new RunProfileDefinition { Name = "p", Sources = sources }, saveProfile: true, cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public void Sources_collect_in_order_under_their_alias_or_position()
    {
        var single = Write("app/web.config");
        Write("app/logs/a.log");
        Write("app/logs/b.tmp");
        Write("app/logs/deep/c.log");

        var result = Run(
            new RunProfileSource { Path = single },
            new RunProfileSource { Path = Path.Join(Data, "app", "logs"), Alias = "all logs", Exclude = ["*.tmp"] },
            new RunProfileSource { Path = Path.Join(Data, "app", "logs", "**", "*.log") });

        result.Timestamp.Should().Be("20250301T083000Z");
        result.Baseline.Should().Be(single);
        result.Sources.Select(source => (source.Directory, string.Join(",", source.Matched))).Should().Equal(
            ("source_00", "web.config"),
            ("all-logs", "a.log,deep/c.log"),
            ("source_02", "a.log,c.log"));
        File.Exists(Path.Join(result.OutputDir, "all-logs", "deep", "c.log")).Should().BeTrue();
        File.Exists(Path.Join(result.OutputDir, "all-logs", "b.tmp")).Should().BeFalse();
        result.Files.Should().HaveCount(5).And.OnlyContain(file => file.Size == 10 && file.Sha256.Length == 64);
    }

    [Fact]
    public void Optional_sources_skip_and_required_ones_refuse()
    {
        var present = Write("a.ini");
        var result = Run(
            new RunProfileSource { Path = present },
            new RunProfileSource { Path = Path.Join(Data, "gone.ini"), Optional = true },
            new RunProfileSource { Path = Path.Join(Data, "*.none"), Optional = true });

        result.Sources.Skip(1).Select(source => (source.Skipped, source.Reason)).Should().Equal((true, "missing"), (true, "no-matches"));
        FluentActions.Invoking(() => Run(new RunProfileSource { Path = present }, new RunProfileSource { Path = Path.Join(Data, "*.none") }))
            .Should().Throw<RunProfileException>().WithMessage("sources[1].path: matches nothing: *");
    }

    [Fact]
    public void The_profiles_root_and_links_are_never_collected()
    {
        Write("a.ini");
        File.WriteAllText(Path.Join(_tmp.FullName, "top.ini"), "x");
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Join(Data, "link.ini"), Path.Join(Data, "a.ini"));
        }

        _ = Run(new RunProfileSource { Path = _tmp.FullName });
        var second = Run(new RunProfileSource { Path = _tmp.FullName });

        second.Sources[0].Matched.Should().BeEquivalentTo(["data/a.ini", "top.ini"]);
    }

    [Fact]
    public void Secrets_are_redacted_unless_ignored_and_metadata_matches_the_result()
    {
        var secret = Write("config.txt", "password = Hunter12345\n");

        var result = Run(new RunProfileSource { Path = secret });
        File.ReadAllText(result.Files[0].Destination).Should().Contain("[SECRET]");
        result.Secrets.RulesLoaded.Should().BeTrue();
        result.Secrets.Findings.Should().ContainSingle().Which.Rule.Should().Be("PasswordAssignment");

        var metadata = JsonSerializer.Deserialize(File.ReadAllText(Path.Join(result.OutputDir, "metadata.json")), ModelJson.TypeInfo<RunProfileRunResult>());
        metadata.Should().BeEquivalentTo(result);

        var ignored = new RunProfileExecutor(_tmp.FullName, new FixedTimeProvider(Now.AddHours(1))).Execute(
            new RunProfileDefinition { Name = "p", Sources = [new RunProfileSource { Path = secret }], SecretScanner = new SecretScannerOptions { IgnoreRules = ["PasswordAssignment"] } },
            saveProfile: false,
            cancellationToken: TestContext.Current.CancellationToken);
        File.ReadAllText(ignored.Files[0].Destination).Should().Contain("Hunter12345");
        ignored.Secrets.Findings.Should().BeEmpty();
        ignored.Secrets.IgnoredRules.Should().Equal("PasswordAssignment");
    }

    [Theory]
    [InlineData("logs/app.tmp", "*.tmp", true)]
    [InlineData("logs/app.log", "logs/*.log", true)]
    [InlineData("logs/app.log", "*.tmp", false)]
    public void Excludes_match_the_relative_path_or_the_name(string relative, string pattern, bool excluded)
        => RunProfileExecutor.ShouldExclude(relative, [pattern]).Should().Be(excluded);

    [Fact]
    public void Console_run_reads_a_profile_file_and_applies_overrides()
    {
        var secret = Write("config.txt", "password = Hunter12345\n");
        var file = Path.Join(_tmp.FullName, "p.json");
        File.WriteAllText(file, ModelJson.Serialize(new RunProfileDefinition { Name = "file", Sources = [new RunProfileSource { Path = secret }] }));

        var result = RunProfileCommands.Run(file, null, _tmp.FullName, save: true, "fixed", ["PasswordAssignment"], null, TestContext.Current.CancellationToken);

        result.Timestamp.Should().Be("fixed");
        result.Profile.SecretScanner.IgnoreRules.Should().Equal("PasswordAssignment");
        RunProfileStore.Load("file", _tmp.FullName).SecretScanner.IgnoreRules.Should().Equal("PasswordAssignment");
    }
}
