using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Tests.Profiles.Run;

/// <summary><c>profile.json</c>: strict reads, validation before every save, listing and the console commands over them.</summary>
public sealed class RunProfileStoreTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-profiles-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string File(string name, string text = "x")
    {
        var path = Path.Join(_tmp.FullName, "data", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    private RunProfileDefinition Profile(string name = "nightly") => new()
    {
        Name = name,
        Description = "every night",
        Sources = [new RunProfileSource { Path = File("app.config") }, new RunProfileSource { Path = Path.Join(_tmp.FullName, "gone", "*.log"), Alias = "logs", Optional = true, Exclude = ["*.tmp"] }],
        Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["env"] = "prod" },
        SecretScanner = new SecretScannerOptions { IgnoreRules = ["AwsKey"], IgnorePatterns = ["^#"] },
    };

    [Fact]
    public void A_saved_profile_reads_back_as_written()
    {
        var directory = RunProfileStore.Save(Profile("night shift"), _tmp.FullName);

        Path.GetFileName(directory).Should().Be("night-shift");
        RunProfileStore.Load("night shift", _tmp.FullName).Should().BeEquivalentTo(Profile("night shift"));
        System.IO.File.ReadAllText(Path.Join(directory, RunProfileStore.FileName)).Should().Contain("\"ignore_patterns\": [").And.EndWith("}\n");
    }

    [Fact]
    public void Profiles_list_by_directory_name()
    {
        RunProfileStore.Save(Profile("b"), _tmp.FullName);
        RunProfileStore.Save(Profile("a"), _tmp.FullName);
        Directory.CreateDirectory(Path.Join(RunProfileStore.ProfilesRoot(_tmp.FullName), "empty"));

        RunProfileStore.List(_tmp.FullName, TestContext.Current.CancellationToken).Select(profile => profile.Name).Should().Equal("a", "b");
        RunProfileCommands.ListProfileLines(_tmp.FullName, TestContext.Current.CancellationToken).Should().Equal("- a every night", "- b every night");
    }

    [Theory]
    [InlineData("""{"name": "p", "sources": [{"path": "x"}], "colour": "red"}""", "$.colour")]
    [InlineData("""{"name": "p", "sources": ["x"]}""", "$.sources[0]")]
    [InlineData("""{"sources": [{"path": "x"}]}""", "name")]
    [InlineData("""{"name": "p", "sources": [{"path": "x", "optional": "yes"}]}""", "$.sources[0].optional")]
    public void A_file_the_model_does_not_describe_is_refused_with_its_path(string json, string where)
    {
        var path = Path.Join(_tmp.FullName, "profile.json");
        System.IO.File.WriteAllText(path, json);

        FluentActions.Invoking(() => RunProfileStore.Read(path))
            .Should().Throw<RunProfileException>().Where(exc => exc.Message.StartsWith(path + ": ", StringComparison.Ordinal) && exc.Message.Contains(where, StringComparison.Ordinal));
    }

    [Fact]
    public void Validation_names_the_field()
    {
        void Refused(RunProfileDefinition profile, string message)
            => FluentActions.Invoking(() => RunProfileStore.Save(profile, _tmp.FullName)).Should().Throw<RunProfileException>().WithMessage(message);

        Refused(Profile() with { Name = " " }, "name: required.");
        Refused(Profile() with { Sources = [] }, "sources: at least one source is required.");
        Refused(Profile() with { Sources = [new RunProfileSource { Path = " " }] }, "sources[0].path: required.");
        Refused(Profile() with { Sources = [new RunProfileSource { Path = "/nowhere/app.config" }] }, "sources[0].path: does not exist: /nowhere/app.config");
        Refused(Profile() with { Baseline = "elsewhere" }, "baseline: must be one of the source paths.");
        Refused(Profile() with { SecretScanner = new SecretScannerOptions { IgnorePatterns = ["("] } }, "secret_scanner.ignore_patterns[0]: *");
        FluentActions.Invoking(() => RunProfileStore.Load("missing", _tmp.FullName)).Should().Throw<RunProfileException>().WithMessage("Profile not found: missing");
    }

    [Fact]
    public void Paths_expand_home_and_environment_variables()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        RunProfileStore.Expand("~").Should().Be(home);
        RunProfileStore.Expand("~/app").Should().Be(home + "/app");
        Environment.SetEnvironmentVariable("DRIFTBUSTER_TEST_ROOT", "/srv");
        RunProfileStore.Expand("%DRIFTBUSTER_TEST_ROOT%/app").Should().Be("/srv/app");
        RunProfileStore.SafeName("a b/ç_1-2").Should().Be("a-b-ç_1-2");
    }

    [Fact]
    public void Console_create_parses_options_and_secret_overrides_append()
    {
        var created = RunProfileCommands.Create("cli", null, [File("a.ini")], null, ["k = v", "k=w", "e="], [" R ", "R", ""], null, _tmp.FullName);

        created.Options.Should().Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "w", ["e"] = "" });
        created.SecretScanner.IgnoreRules.Should().Equal("R");
        RunProfileCommands.WithSecretOverrides(created, ["S", "R"], ["p"]).SecretScanner.Should().BeEquivalentTo(new SecretScannerOptions { IgnoreRules = ["R", "S"], IgnorePatterns = ["p"] });
        FluentActions.Invoking(() => RunProfileCommands.ParseOptions(["novalue"])).Should().Throw<RunProfileException>().WithMessage("Invalid option 'novalue': use key=value.");
    }
}
