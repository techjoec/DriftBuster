using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    [InlineData("   ", "")]
    public void SlugifyLowercasesAndReplacesSeparators(string value, string expected)
        => ConfigIdentity.Slugify(value).Should().Be(expected);

    private static DetectionMatch Match(string format, string plugin, params (string Key, object? Value)[] metadata)
    {
        var values = new JsonObject();
        foreach (var (key, value) in metadata)
        {
            values[key] = JsonSerializer.SerializeToNode(value);
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
    public void Cache_entries_hold_the_canonical_text_for_their_signature()
    {
        var cache = new DiffCache(Path.Combine(_tmp.FullName, "cache"));

        cache.Save("host", "cfg", "sig", "canonical é\n");

        var path = cache.EntryPath("host", "cfg");
        Path.GetFileName(path).Should().Be(MultiServerPlan.Sha1Hex("host:cfg") + ".json");
        cache.Load("host", "cfg", "sig").Should().Be("canonical é\n");
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
