using System.Numerics;
using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Tests.Secrets;

/// <summary>Branches of <c>secret_scanning</c> that the mirrored Python tests and the oracle cases do not reach.</summary>
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

    // Expected: ProfileRunResult.secrets from CPython 3.13 execute_profile over the same file, options and scanner.
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

        PythonJson.TryLoads(expected, out var want).Should().BeTrue();
        PythonRepr.Repr(SecretScanner.RunSecretsMetadata(context, logs)).Should().Be(PythonRepr.Repr(want));
    }

    [Fact]
    public void InvalidResourceJsonRaises()
    {
        SecretScanner.ResetSecretRuleCache();
        SecretScanner.ResourceReader = () => "{not json";

        var load = SecretScanner.LoadSecretRules;

        load.Should().Throw<InvalidDataException>();
    }

    // Expected values from CPython 3.13 load_secret_rules() with the resource text replaced.
    [Theory]
    [InlineData("null", "none", false)]
    [InlineData("{}", "unknown", true)]
    [InlineData("""{"version": null}""", "None", true)]
    [InlineData("""{"version": 5, "rules": []}""", "5", true)]
    public void ResourcePayloadShapesFollowLoadSecretRules(string text, string version, bool loaded)
    {
        SecretScanner.ResetSecretRuleCache();
        SecretScanner.ResourceReader = () => text;

        var (rules, actualVersion, actualLoaded) = SecretScanner.LoadSecretRules();

        rules.Should().BeEmpty();
        actualVersion.Should().Be(version);
        actualLoaded.Should().Be(loaded);
    }

    [Theory]
    [InlineData("[1]", "'list' object has no attribute 'get'")]
    [InlineData("\"x\"", "'str' object has no attribute 'get'")]
    [InlineData("0", "'int' object has no attribute 'get'")]
    public void NonMappingResourcePayloadRaisesLikeAttributeError(string text, string message)
    {
        SecretScanner.ResetSecretRuleCache();
        SecretScanner.ResourceReader = () => text;

        var load = SecretScanner.LoadSecretRules;

        load.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public void LooksBinaryIsFalseForAMissingFile()
    {
        SecretScanner.LooksBinary(Path.Combine(_tmp.FullName, "missing")).Should().BeFalse();
    }

    [Fact]
    public void LooksBinaryOnlyReadsTheFirst1024Bytes()
    {
        var late = Path.Combine(_tmp.FullName, "late.bin");
        File.WriteAllBytes(late, [.. Enumerable.Repeat((byte)'a', 1024), 0]);
        var early = Path.Combine(_tmp.FullName, "early.bin");
        File.WriteAllBytes(early, [.. Enumerable.Repeat((byte)'a', 1023), 0]);

        SecretScanner.LooksBinary(late).Should().BeFalse();
        SecretScanner.LooksBinary(early).Should().BeTrue();
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

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(0L, false)]
    [InlineData(2L, true)]
    [InlineData(0.0, false)]
    [InlineData(0.5, true)]
    public void IsTruthyFollowsPython(object value, bool expected)
    {
        SecretScanner.IsTruthy(value).Should().Be(expected);
    }

    [Fact]
    public void IsTruthyCoversBigIntegersSequencesAndObjects()
    {
        SecretScanner.IsTruthy(BigInteger.Zero).Should().BeFalse();
        SecretScanner.IsTruthy(BigInteger.One).Should().BeTrue();
        SecretScanner.IsTruthy(Enumerable.Empty<int>().Select(item => item)).Should().BeFalse();
        SecretScanner.IsTruthy(new object()).Should().BeTrue();
    }

    // Fix g: Python's copy_with_secret_filter never returns on these lines (each replacement puts a new match inside the
    // placeholder it inserted). The port follows Python to the guard budget, goes back to the line's last replacement that
    // consumed source text, stops the rule on that line, records the guard and carries on.
    [Theory(Timeout = 30_000)]
    [InlineData("secret", "token secret\nplain secret\nnone\n", "token [SECRET]\nplain [SECRET]\nnone\n", new[] { 1, 2 })]
    [InlineData("\u017f+", "a\u017f\u017fb\nsS\n", "a[SECRET]b\n[SECRET]\n", new[] { 1, 2 })]
    public async Task ARuleMatchingInsideItsOwnReplacementIsStoppedOnThatLine(string pattern, string input, string expected, int[] guardedLines)
    {
        var source = Path.Combine(_tmp.FullName, "loop.txt");
        File.WriteAllText(source, input, new UTF8Encoding(false));
        var context = new SecretDetectionContext(
            rules: [new SecretDetectionRule("SelfMatching", PythonPattern.Compile(pattern, PythonReFlags.IgnoreCase))],
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
                new SecretDetectionRule("SelfMatching", PythonPattern.Compile("secret", PythonReFlags.IgnoreCase)),
                new SecretDetectionRule("Pw", PythonPattern.Compile("pw=\\d")),
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

    // Fix g, two rules taking turns inside each other's [SECRET] (Python never returns): the line goes back to its last
    // replacement that consumed source text and both looping rules are stopped, in the order they first replaced inserted text.
    [Fact(Timeout = 30_000)]
    public async Task RulesTakingTurnsInsideInsertedTextAreAllStopped()
    {
        var (output, context, logs) = await CopyAsync(
            "alternating.txt",
            "SEC\nSEC plain\n",
            new SecretDetectionRule("NoDoubleBracketSE", PythonPattern.Compile("(?<!\\[\\[)SE")),
            new SecretDetectionRule("TBracket", PythonPattern.Compile("T\\]")));

        output.Should().Be("[SECRET]C\n[SECRET]C plain\n".ReplaceLineEndings());
        context.RedactionGuards.Should().Equal(
            new SecretRedactionGuard("alternating.txt", "NoDoubleBracketSE", 1),
            new SecretRedactionGuard("alternating.txt", "TBracket", 1),
            new SecretRedactionGuard("alternating.txt", "NoDoubleBracketSE", 2),
            new SecretRedactionGuard("alternating.txt", "TBracket", 2));
        context.Findings.Select(finding => (finding.Rule, finding.Line, finding.Snippet)).Should().Equal(
            ("NoDoubleBracketSE", 1, "[SECRET]C"), ("NoDoubleBracketSE", 2, "[SECRET]C plain"));
        logs.Should().Equal(
            "secret candidate redacted (NoDoubleBracketSE) from alternating.txt:1 -> [SECRET]C",
            "secret candidate redacted (NoDoubleBracketSE) from alternating.txt:2 -> [SECRET]C plain",
            "scrubbed 2 potential secret line(s) from alternating.txt");
    }

    // CPython returns "[[[SECRET]RET]RET]" with three findings: a rule that replaces inside inserted text a few times and then
    // stops matching is followed exactly.
    [Fact(Timeout = 30_000)]
    public async Task ReplacementsInsideInsertedTextThatEndAreFollowedExactly()
    {
        var (output, context, logs) = await CopyAsync(
            "three.txt", "SEC\n", new SecretDetectionRule("SecNotAfterThreeBrackets", PythonPattern.Compile("(?<!\\[\\[\\[)SEC")));

        output.Should().Be("[[[SECRET]RET]RET]\n".ReplaceLineEndings());
        context.RedactionGuards.Should().BeEmpty();
        context.Findings.Select(finding => finding.Snippet).Should().Equal("[SECRET]", "[[SECRET]RET]", "[[[SECRET]RET]RET]");
        logs[^1].Should().Be("scrubbed 3 potential secret line(s) from three.txt");
    }

    // (?<!\[{n})SEC replaces n - 1 times inside inserted text (each growing the line) and then stops. At n = 1025 that is exactly
    // GuardBudget replacements and CPython's output is reproduced ("[" * n + "SECRET]" + "RET]" * (n - 1), n findings); one more
    // is past the budget and the guard applies.
    [Theory(Timeout = 60_000)]
    [InlineData(SecretScanner.GuardBudget + 1, false)]
    [InlineData(SecretScanner.GuardBudget + 2, true)]
    public async Task TheGuardBudgetCountsReplacementsInsideInsertedText(int brackets, bool guarded)
    {
        var (output, context, _) = await CopyAsync(
            "budget.txt", "SEC\n", new SecretDetectionRule("Counting", PythonPattern.Compile($"(?<!\\[{{{brackets}}})SEC")));

        var python = new string('[', brackets) + "SECRET]" + string.Concat(Enumerable.Repeat("RET]", brackets - 1)) + "\n";
        output.Should().Be(guarded ? "[SECRET]\n".ReplaceLineEndings() : python.ReplaceLineEndings());
        context.Findings.Should().HaveCount(guarded ? 1 : brackets);
        var expectedGuards = guarded ? new[] { new SecretRedactionGuard("budget.txt", "Counting", 1) } : Array.Empty<SecretRedactionGuard>();
        context.RedactionGuards.Should().Equal(expectedGuards);
    }

    private async Task<(string Output, SecretDetectionContext Context, List<string> Logs)> CopyAsync(
        string name, string input, params SecretDetectionRule[] rules)
    {
        var source = Path.Combine(_tmp.FullName, name);
        File.WriteAllText(source, input, new UTF8Encoding(false));
        var context = new SecretDetectionContext(
            rules: rules,
            version: "v1",
            ignoreRules: new HashSet<string>(StringComparer.Ordinal),
            ignorePatterns: [],
            ignorePatternText: [],
            rulesLoaded: true);
        var destination = Path.Combine(_tmp.FullName, "out", name);
        var logs = new List<string>();
        await Task.Run(
            () => SecretScanner.CopyWithSecretFilter(source, destination, name, context, logs.Add, _ => false, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        return (File.ReadAllText(destination), context, logs);
    }
}
