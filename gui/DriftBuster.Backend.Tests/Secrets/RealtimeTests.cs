using System.Text;
using System.Text.Json;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Tests.Secrets;

/// <summary>
/// Mirror of tests/secret_scanning/test_realtime.py. Python drives <c>run_profiles.execute_profile</c>; the run-profile
/// executor (profile store, source expansion, destination layout) is ported in phase 6. Its secret path is ported now and
/// these tests run exactly that path: <c>build_context(profile.options, profile.secret_scanner)</c> with the scanner mapping
/// as given, <c>copy_with_secret_filter</c> into <c>raw/&lt;run&gt;/source_00/&lt;name&gt;</c> with the display path relative to the
/// source's parent and a log collector, then <c>secret_metadata</c> (<see cref="SecretScanner.RunSecretsMetadata"/>), written to
/// and read back from <c>metadata.json</c> under <c>secrets</c>, and kept as <c>result.secrets</c>. The assertions are Python's.
/// Both sources are single files, for which <c>_copy_file</c> takes <c>base = match.parent</c>, so the display path
/// <c>relative.as_posix()</c> is the file name. Phase 6 replaces <see cref="ExecuteSecretPath"/> with the ported
/// <c>execute_profile</c>.
/// </summary>
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

    private sealed record SecretRun(string CopiedContent, OrderedDictionary<string, object?> Metadata, OrderedDictionary<string, object?> ResultSecrets);

    private SecretRun ExecuteSecretPath(string secretFile, IReadOnlyDictionary<string, object?> secretScanner)
    {
        var options = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var context = SecretScanner.BuildContext(options, secretScanner);
        var logs = new List<string>();
        var runRoot = Path.Combine(_tmp.FullName, "Profiles", "secret", "raw", "run");
        var name = Path.GetFileName(secretFile);
        var destination = Path.Combine(runRoot, "source_00", name);
        SecretScanner.CopyWithSecretFilter(secretFile, destination, name, context, logs.Add);

        var secrets = SecretScanner.RunSecretsMetadata(context, logs);
        var metadataPath = Path.Combine(runRoot, "metadata.json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal) { ["secrets"] = secrets }), new UTF8Encoding(false));
        PythonJson.TryLoads(File.ReadAllText(metadataPath, Encoding.UTF8), out var metadata).Should().BeTrue();
        return new SecretRun(File.ReadAllText(destination, Encoding.UTF8), (OrderedDictionary<string, object?>)metadata!, secrets);
    }

    [Fact]
    public void RunProfileRedactsSecretLines()
    {
        var secretFile = Path.Combine(_tmp.FullName, "config.txt");
        File.WriteAllText(secretFile, "password = Hunter12345\n", new UTF8Encoding(false));

        var run = ExecuteSecretPath(secretFile, new OrderedDictionary<string, object?>(StringComparer.Ordinal));

        run.CopiedContent.Should().Contain("[SECRET]");
        var secrets = (OrderedDictionary<string, object?>)run.Metadata["secrets"]!;
        secrets["rules_loaded"].Should().Be(true);
        var findings = secrets["findings"].Should().BeOfType<List<object?>>().Which;
        findings.Should().NotBeEmpty();
        var finding = (OrderedDictionary<string, object?>)findings[0]!;
        finding["rule"].Should().Be("PasswordAssignment");
        ((string)finding["path"]!).Should().EndWith("config.txt");
        ((string)finding["snippet"]!).Should().Contain("[SECRET]");

        run.ResultSecrets.Should().NotBeNull();
        ((List<object?>)run.ResultSecrets["findings"]!).Should().NotBeEmpty();
    }

    [Fact]
    public void RunProfileRespectsIgnoreRules()
    {
        var secretFile = Path.Combine(_tmp.FullName, "config_ignore.txt");
        File.WriteAllText(secretFile, "password = Hunter12345\n", new UTF8Encoding(false));
        var scanner = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ignore_rules"] = new List<object?> { "PasswordAssignment" },
        };

        var run = ExecuteSecretPath(secretFile, scanner);

        run.CopiedContent.Should().Contain("Hunter12345");
        var secrets = (OrderedDictionary<string, object?>)run.Metadata["secrets"]!;
        secrets["findings"].Should().BeOfType<List<object?>>().Which.Should().BeEmpty();
        run.ResultSecrets.Should().NotBeNull();
        ((List<object?>)run.ResultSecrets["findings"]!).Should().BeEmpty();
    }
}
