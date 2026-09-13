using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Tests.Secrets;

/// <summary>
/// <see cref="SecretScanner"/> against CPython on <c>Data/secret_cases.json</c> (written by
/// <c>tools/parity/gen_hunt_secret_cases.py</c>): <c>copy_with_secret_filter</c> output bytes, size, digest, findings and
/// log lines plus the built context; <c>secret_option_values</c>; <c>compile_ruleset_from_mapping</c>; and the packaged
/// ruleset. The oracle was written on Linux, where <c>write_text</c> keeps "\n"; on Windows a redacted copy uses "\r\n",
/// so the byte-level comparison of redacted copies runs on non-Windows hosts only.
/// </summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class SecretOracleTests : IDisposable
{
    private static readonly Lazy<OrderedDictionary<string, object?>> Data = new(() =>
    {
        var path = Path.Combine(RepoPaths.Root, "gui", "DriftBuster.Backend.Tests", "Secrets", "Data", "secret_cases.json");
        PythonJson.TryLoads(File.ReadAllText(path), out var value).Should().BeTrue();
        return (OrderedDictionary<string, object?>)value!;
    });

    private readonly SecretRuleCacheIsolation _isolation = new();
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-secret-oracle-");

    public void Dispose()
    {
        _isolation.Dispose();
        _tmp.Delete(recursive: true);
    }

    public static TheoryData<string> CopyNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var entry in Section("copies"))
            {
                names.Add((string)entry["name"]!);
            }

            return names;
        }
    }

    private static List<OrderedDictionary<string, object?>> Section(string name)
        => ((List<object?>)Data.Value[name]!).Cast<OrderedDictionary<string, object?>>().ToList();

    [Theory]
    [MemberData(nameof(CopyNames))]
    public void CopyWithSecretFilterMatchesPython(string name)
    {
        var entry = Section("copies").Single(item => string.Equals((string)item["name"]!, name, StringComparison.Ordinal));
        var source = Path.Combine(_tmp.FullName, name + ".src");
        File.WriteAllBytes(source, Convert.FromBase64String((string)entry["content"]!));
        var destination = Path.Combine(_tmp.FullName, "out", name + ".txt");
        SecretScanner.ResetSecretRuleCache();
        var context = SecretScanner.BuildContext(
            entry["options"] as IReadOnlyDictionary<string, object?>,
            entry["scanner"] as IReadOnlyDictionary<string, object?>);
        var logs = new List<string>();

        var (size, digest) = SecretScanner.CopyWithSecretFilter(source, destination, $"dir/{name}.txt", context, logs.Add, cancellationToken: TestContext.Current.CancellationToken);

        var expectedContext = (OrderedDictionary<string, object?>)entry["context"]!;
        context.Rules.Select(rule => rule.Name).Should().Equal(Strings(expectedContext["rules"]));
        context.Version.Should().Be((string)expectedContext["version"]!);
        context.IgnoreRules.Order(StringComparer.Ordinal).Should().Equal(Strings(expectedContext["ignore_rules"]));
        context.IgnorePatternText.Should().Equal(Strings(expectedContext["ignore_pattern_text"]));
        context.IgnorePatterns.Select(pattern => pattern.Pattern).Should().Equal(Strings(expectedContext["ignore_patterns"]));
        context.RulesLoaded.Should().Be((bool)expectedContext["rules_loaded"]!);
        context.Findings.Select(finding => PythonRepr.Repr(new List<object?> { finding.Path, finding.Rule, finding.Line, finding.Snippet }))
            .Should().Equal(((List<object?>)entry["findings"]!).Select(PythonRepr.Repr));
        logs.Should().Equal(Strings(entry["logs"]));
        if (!OperatingSystem.IsWindows() || context.Findings.Count == 0)
        {
            Convert.ToBase64String(File.ReadAllBytes(destination)).Should().Be((string)entry["output"]!);
            size.Should().Be((int)entry["size"]!);
            digest.Should().Be((string)entry["sha256"]!);
        }
    }

    [Fact]
    public void SecretOptionValuesMatchPython()
    {
        foreach (var entry in Section("option_values"))
        {
            SecretScanner.SecretOptionValues(entry["value"]).Should().Equal(Strings(entry["result"]), PythonRepr.Repr(entry["value"]));
        }
    }

    [Fact]
    public void CompileRulesetFromMappingMatchesPython()
    {
        foreach (var entry in Section("compile"))
        {
            var compiled = SecretScanner.CompileRulesetFromMapping(entry["payload"]);
            object? rendered = compiled is null
                ? null
                : new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["version"] = compiled.Version,
                    ["rules"] = compiled.Rules.Select(rule => (object?)new List<object?>
                    {
                        rule.Name,
                        rule.Pattern.Pattern,
                        rule.Pattern.Flags.HasFlag(PythonReFlags.IgnoreCase),
                        rule.Description,
                    }).ToList(),
                };
            PythonRepr.Repr(rendered).Should().Be(PythonRepr.Repr(entry["result"]), PythonRepr.Repr(entry["payload"]));
        }
    }

    [Fact]
    public void PackagedRulesMatchPython()
    {
        var packaged = (OrderedDictionary<string, object?>)Data.Value["packaged"]!;
        SecretScanner.ResetSecretRuleCache();
        var (rules, version, loaded) = SecretScanner.LoadSecretRules();
        version.Should().Be((string)packaged["version"]!);
        loaded.Should().Be((bool)packaged["loaded"]!);
        PythonRepr.Repr(rules.Select(rule => (object?)new List<object?> { rule.Name, rule.Pattern.Pattern, rule.Description }).ToList())
            .Should().Be(PythonRepr.Repr(packaged["rules"]));
    }

    [Fact]
    public void MissingResourceCachesNoRulesVersionNone()
    {
        SecretScanner.ResetSecretRuleCache();
        SecretScanner.ResourceReader = () => null;

        var (rules, version, loaded) = SecretScanner.LoadSecretRules();

        rules.Should().BeEmpty();
        version.Should().Be("none");
        loaded.Should().BeFalse();
        var context = SecretScanner.BuildContext(null, null);
        context.RulesLoaded.Should().BeFalse();
    }

    [Fact]
    public void ResourceWithoutUsableRulesIsLoadedWithItsVersion()
    {
        SecretScanner.ResetSecretRuleCache();
        SecretScanner.ResourceReader = () => """{"version": "2", "rules": [{"name": "x", "pattern": "("}]}""";

        SecretScanner.LoadSecretRules().Should().Be(((IReadOnlyList<SecretDetectionRule>)SecretScanner.RuleCache!, "2", true));
        SecretScanner.RuleCache.Should().BeEmpty();
    }

    private static List<string> Strings(object? value) => ((List<object?>)value!).Cast<string>().ToList();
}
