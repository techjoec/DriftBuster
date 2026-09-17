using System.Globalization;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Backend.Tests.MultiServer;

/// <summary>
/// Multi-server pieces: slugs, config ids, the baseline choice, the cache file and plan parsing.
/// </summary>
public sealed class MultiServerUnitTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-multi-server-unit-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Theory]
    [InlineData("  Web.Config ", "web-config")]
    [InlineData("Ünïcödé/Файл.json", "ünïcödé/файл-json")]
    [InlineData("a b\tc", "a-b-c")]
    [InlineData("½Ⅷx", "½ⅷx")]
    [InlineData("😀.json", "--json")]
    [InlineData("İ", "i-")]
    [InlineData("   ", "")]
    public void SlugifyLowercasesAndReplacesSeparators(string value, string expected)
        => ConfigIdentity.Slugify(value).Should().Be(expected);

    private static DetectionMatch Match(string format, string plugin, params (string Key, object? Value)[] metadata)
    {
        var values = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            values[key] = value;
        }

        return new DetectionMatch(plugin, format, null, 0.9, [], values);
    }

    [Fact]
    public void ConfigIdUsesCatalogFormatVariantAndRelativePath()
    {
        var match = Match("xml", "xml", ("catalog_format", "structured-config-xml"), ("catalog_variant", " Web-Config "), ("config_original_filename", "web.config"));

        ConfigIdentity.NormaliseConfigId(match, "app1/Web.config").Should().Be("structured-config-xml/web-config/app1/web-config");
    }

    [Fact]
    public void ConfigIdFallsBackToFormatNameAndSkipsBlankVariant()
    {
        var match = Match("ini", "ini", ("catalog_format", ""), ("catalog_variant", "  "));

        ConfigIdentity.NormaliseConfigId(match, "conf/app.ini").Should().Be("ini/conf/app-ini");
    }

    [Fact]
    public void DisplayNamePrefersOriginalFilename()
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["config_original_filename"] = "  web.config " };

        ConfigIdentity.DisplayName(metadata, "app/web.config").Should().Be("web.config");
        ConfigIdentity.DisplayName(new Dictionary<string, object?>(StringComparer.Ordinal) { ["config_original_filename"] = " " }, "app/web.config").Should().Be("app/web.config");
        ConfigIdentity.DisplayName(null, "app/web.config").Should().Be("app/web.config");
    }

    private static MultiServerPlan Plan(string host, bool preferred = false, int priority = 0)
        => new() { HostId = host, Label = host, IsPreferred = preferred, Priority = priority };

    [Fact]
    public void BaselinePrefersPreferredThenPriorityThenPlanOrder()
    {
        BaselineSelector.Select([Plan("a", priority: 9), Plan("b", preferred: true), Plan("c", preferred: true, priority: 1)]).HostId.Should().Be("c");
        BaselineSelector.Select([Plan("a", priority: 1), Plan("b", priority: 5), Plan("c", priority: 5)]).HostId.Should().Be("b");
        BaselineSelector.Select([Plan("a"), Plan("b")]).HostId.Should().Be("a");
        var empty = () => BaselineSelector.Select([]);
        empty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CacheSaveWritesSortedCompactJson()
    {
        var cache = new DiffCache(Path.Combine(_tmp.FullName, "cache"));
        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["b"] = 1,
            ["a"] = new List<object?> { 1, 2.5, new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["z"] = null, ["y"] = true } },
            ["u"] = "é\n",
        };

        cache.Save("host", "cfg", "sig", payload, TestContext.Current.CancellationToken);

        var path = cache.EntryPath("host", "cfg");
        Path.GetFileName(path).Should().Be(MultiServerPlan.Sha1Hex("host:cfg") + ".json");
        File.ReadAllText(path).Should().Be("{\"a\": [1, 2.5, {\"y\": true, \"z\": null}], \"b\": 1, \"signature\": \"sig\", \"u\": \"é\\n\"}");
        cache.Load("host", "cfg", "sig")!["b"].Should().Be(1);
        cache.Load("host", "cfg", "other").Should().BeNull();
        cache.Load("host", "missing", "sig").Should().BeNull();
        Directory.EnumerateFiles(cache.Root).Should().ContainSingle();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"signature\": 7}")]
    public void CacheLoadIgnoresUnusableEntries(string content)
    {
        var cache = new DiffCache(Path.Combine(_tmp.FullName, "cache"));
        File.WriteAllText(cache.EntryPath("host", "cfg"), content);

        cache.Load("host", "cfg", "7").Should().BeNull();
    }

    [Fact]
    public void PlanFromServerScanPlanStripsAndExpands()
    {
        var plan = new ServerScanPlan
        {
            HostId = "  host-1 ",
            Label = "   ",
            Roots = ["  ", " /srv/app ", string.Empty],
            Baseline = new ServerScanBaselinePreference { IsPreferred = true, Priority = 4 },
            ThrottleSeconds = 0.5,
        };

        var parsed = MultiServerPlan.FromServerScanPlan(plan);

        parsed.HostId.Should().Be("host-1");
        parsed.Label.Should().Be("host-1");
        parsed.Roots.Should().Equal("/srv/app");
        parsed.IsPreferred.Should().BeTrue();
        parsed.Priority.Should().Be(4);
        parsed.ThrottleSeconds.Should().Be(0.5);
        MultiServerPlan.FromServerScanPlan(new ServerScanPlan()).HostId.Should().MatchRegex("^[0-9a-f]{40}$");
    }
}
