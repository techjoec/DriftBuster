using System.Numerics;
using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.EngineRe;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Tests.Secrets;

/// <summary>Secret scanner metadata, rule resource loading, binary detection and the redaction guard.</summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class SecretScannerEdgeTests : IDisposable
{
    private readonly SecretRuleCacheIsolation _isolation = new();
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-secret-edges-");

    public void Dispose()
    {
        _isolation.Dispose();
        _tmp.Delete(recursive: true);
    }

    [Fact]
    public void RunSecretsMetadataMatchesExecuteProfile()
    {
        const string expected = """
            {"ruleset_version": "2024-06-01", "rules_loaded": true, "ignored_rules": ["Alpha", "Zed"], "ignored_patterns": ["NOPE", "(x"],
             "findings": [{"path": "config.txt", "rule": "PasswordAssignment", "line": 1, "snippet": "[SECRET]"},
                          {"path": "config.txt", "rule": "AwsAccessKeyId", "line": 2, "snippet": "[SECRET]"}],
             "messages": ["secret candidate redacted (PasswordAssignment) from config.txt:1 -> [SECRET]",
                          "secret candidate redacted (AwsAccessKeyId) from config.txt:2 -> [SECRET]",
                          "scrubbed 2 potential secret line(s) from config.txt"]}
            """;
        var source = Path.Combine(_tmp.FullName, "config.txt");
        File.WriteAllText(source, "password = Hunter12345\nAKIAABCDEFGHIJKLMNOP\n", new UTF8Encoding(false));
        var options = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["secret_ignore_patterns"] = "NOPE, (x" };
        var scanner = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["ignore_rules"] = new List<object?> { "Zed", "Alpha" } };
        var context = SecretScanner.BuildContext(options, scanner);
        var logs = new List<string>();

        SecretScanner.CopyWithSecretFilter(source, Path.Combine(_tmp.FullName, "out", "config.txt"), "config.txt", context, logs.Add, cancellationToken: TestContext.Current.CancellationToken);

        EngineJson.TryLoads(expected, out var want).Should().BeTrue();
        EngineRepr.Repr(SecretScanner.RunSecretsMetadata(context, logs)).Should().Be(EngineRepr.Repr(want));
    }

    [Fact]
    public void InvalidResourceJsonRaises()
    {
        SecretScanner.ResetSecretRuleCache();
        SecretScanner.ResourceReader = () => "{not json";

        var load = SecretScanner.LoadSecretRules;

        load.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void BinaryDetectorOverrideForcesAVerbatimCopy()
    {
        var source = Path.Combine(_tmp.FullName, "config.txt");
        File.WriteAllText(source, "password = Hunter12345\r\n", new UTF8Encoding(false));
        var context = SecretScanner.BuildContext(null, null);
        var destination = Path.Combine(_tmp.FullName, "nested", "deeper", "config.txt");

        var (size, digest) = SecretScanner.CopyWithSecretFilter(source, destination, "config.txt", context, _ => { }, _ => true, TestContext.Current.CancellationToken);

        File.ReadAllBytes(destination).Should().Equal(File.ReadAllBytes(source));
        size.Should().Be(new FileInfo(source).Length);
        digest.Should().Be(SecretScanner.HashFile(source));
        context.Findings.Should().BeEmpty();
    }

    [Fact]
    public void RedactedCopyKeepsTheSourceModificationTime()
    {
        var source = Path.Combine(_tmp.FullName, "config.txt");
        File.WriteAllText(source, "password = Hunter12345\n", new UTF8Encoding(false));
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, stamp);
        var destination = Path.Combine(_tmp.FullName, "out.txt");

        SecretScanner.CopyWithSecretFilter(source, destination, "config.txt", SecretScanner.BuildContext(null, null), _ => { }, cancellationToken: TestContext.Current.CancellationToken);

        File.GetLastWriteTimeUtc(destination).Should().Be(stamp);
        File.ReadAllText(destination).Should().Contain("[SECRET]");
    }

    // Each replacement puts a new match inside the placeholder it inserted. The scanner stops at the guard budget, goes back to the line's
    // last replacement that consumed source text, stops the rule on that line, records the guard and carries on.
    [Theory(Timeout = 30_000)]
    [InlineData("secret", "token secret\nplain secret\nnone\n", "token [SECRET]\nplain [SECRET]\nnone\n", new[] { 1, 2 })]
    [InlineData("\u017f+", "a\u017f\u017fb\nsS\n", "a[SECRET]b\n[SECRET]\n", new[] { 1, 2 })]
    public async Task ARuleMatchingInsideItsOwnReplacementIsStoppedOnThatLine(string pattern, string input, string expected, int[] guardedLines)
    {
        var source = Path.Combine(_tmp.FullName, "loop.txt");
        File.WriteAllText(source, input, new UTF8Encoding(false));
        var context = new SecretDetectionContext(
            rules: [new SecretDetectionRule("SelfMatching", EnginePattern.Compile(pattern, EngineReFlags.IgnoreCase))],
            version: "v1",
            ignoreRules: new HashSet<string>(StringComparer.Ordinal),
            ignorePatterns: [],
            ignorePatternText: [],
            rulesLoaded: true);
        var destination = Path.Combine(_tmp.FullName, "out", "loop.txt");
        var logs = new List<string>();

        await Task.Run(
            () => SecretScanner.CopyWithSecretFilter(source, destination, "loop.txt", context, logs.Add, _ => false, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        File.ReadAllText(destination).Should().Be(expected.ReplaceLineEndings());
        context.RedactionGuards.Should().Equal(guardedLines.Select(line => new SecretRedactionGuard("loop.txt", "SelfMatching", line)));
        context.Findings.Select(finding => (finding.Line, finding.Snippet)).Should().Equal(
            expected.Split('\n').Select((line, index) => (Line: index + 1, Snippet: line)).Where(item => item.Snippet.Contains("[SECRET]", StringComparison.Ordinal)));
        logs[^1].Should().Be($"scrubbed {guardedLines.Length} potential secret line(s) from loop.txt");
    }

    [Fact]
    public void TheGuardSkipsOnlyTheStoppedRuleAndLaterRulesStillRedact()
    {
        var source = Path.Combine(_tmp.FullName, "mixed.txt");
        File.WriteAllText(source, "secret pw=1\n", new UTF8Encoding(false));
        var context = new SecretDetectionContext(
            rules:
            [
                new SecretDetectionRule("SelfMatching", EnginePattern.Compile("secret", EngineReFlags.IgnoreCase)),
                new SecretDetectionRule("Pw", EnginePattern.Compile("pw=\\d")),
            ],
            version: "v1",
            ignoreRules: new HashSet<string>(StringComparer.Ordinal),
            ignorePatterns: [],
            ignorePatternText: [],
            rulesLoaded: true);
        var destination = Path.Combine(_tmp.FullName, "out", "mixed.txt");

        SecretScanner.CopyWithSecretFilter(source, destination, "mixed.txt", context, _ => { }, _ => false, TestContext.Current.CancellationToken);

        File.ReadAllText(destination).Should().Be("[SECRET] [SECRET]\n".ReplaceLineEndings());
        context.Findings.Select(finding => finding.Rule).Should().Equal("SelfMatching", "Pw");
        context.RedactionGuards.Should().Equal(new SecretRedactionGuard("mixed.txt", "SelfMatching", 1));
    }
}
