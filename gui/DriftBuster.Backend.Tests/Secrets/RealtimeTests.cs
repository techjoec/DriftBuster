using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Tests.Secrets;

/// <summary>Mirror of tests/secret_scanning/test_realtime.py: both tests drive the ported <c>run_profiles.execute_profile</c>.</summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class RealtimeTests : IDisposable
{
    private readonly SecretRuleCacheIsolation _isolation = new();
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-realtime-");

    public void Dispose()
    {
        _isolation.Dispose();
        _tmp.Delete(recursive: true);
    }

    private static OrderedDictionary<string, object?> ReadMetadata(ProfileRunResult result)
    {
        var metadataPath = Path.Combine(result.OutputDir, "metadata.json");
        PythonJson.TryLoads(File.ReadAllText(metadataPath, Encoding.UTF8), out var metadata).Should().BeTrue();
        return (OrderedDictionary<string, object?>)metadata!;
    }

    [Fact]
    public void RunProfileRedactsSecretLines()
    {
        var secretFile = Path.Combine(_tmp.FullName, "config.txt");
        File.WriteAllText(secretFile, "password = Hunter12345\n", new UTF8Encoding(false));

        var profile = new RunProfile("secret", sources: [new(secretFile)]);

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, cancellationToken: TestContext.Current.CancellationToken);

        var copied = result.Files.First(entry => string.Equals(entry.Source, secretFile, StringComparison.Ordinal));
        var content = File.ReadAllText(copied.Destination, Encoding.UTF8);
        content.Should().Contain("[SECRET]");

        var metadata = ReadMetadata(result);
        var secrets = (OrderedDictionary<string, object?>)metadata["secrets"]!;
        secrets["rules_loaded"].Should().Be(true);
        var findings = secrets["findings"].Should().BeOfType<List<object?>>().Which;
        findings.Should().NotBeEmpty();
        var finding = (OrderedDictionary<string, object?>)findings[0]!;
        finding["rule"].Should().Be("PasswordAssignment");
        ((string)finding["path"]!).Should().EndWith("config.txt");
        ((string)finding["snippet"]!).Should().Contain("[SECRET]");

        result.Secrets.Should().NotBeNull();
        ((List<object?>)result.Secrets!["findings"]!).Should().NotBeEmpty();
    }

    [Fact]
    public void RunProfileRespectsIgnoreRules()
    {
        var secretFile = Path.Combine(_tmp.FullName, "config_ignore.txt");
        File.WriteAllText(secretFile, "password = Hunter12345\n", new UTF8Encoding(false));

        var profile = new RunProfile(
            "ignored",
            sources: [new(secretFile)],
            secretScanner: new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["ignore_rules"] = new List<object?> { "PasswordAssignment" } });

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, cancellationToken: TestContext.Current.CancellationToken);

        var copied = result.Files.First(entry => string.Equals(entry.Source, secretFile, StringComparison.Ordinal));
        var content = File.ReadAllText(copied.Destination, Encoding.UTF8);
        content.Should().Contain("Hunter12345");

        var metadata = ReadMetadata(result);
        var secrets = (OrderedDictionary<string, object?>)metadata["secrets"]!;
        secrets["findings"].Should().BeOfType<List<object?>>().Which.Should().BeEmpty();
        result.Secrets.Should().NotBeNull();
        ((List<object?>)result.Secrets!["findings"]!).Should().BeEmpty();
    }
}
