using System.Globalization;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Backend.Tests.MultiServer;

/// <summary>
/// Pieces of <c>driftbuster.multi_server</c> with no Python test of their own: slugs, config ids, the baseline choice, the
/// cache file and plan parsing. Expected values were produced by CPython 3.13 (<c>_slugify</c>, <c>json.dumps</c>).
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
    [InlineData("under_score/dash-", "under_score/dash-")]
    [InlineData("   ", "")]
    [InlineData("Ᲊᲊ.ini", "---ini")]
    [InlineData("Ᲊ.ini", "--ini")]
    [InlineData("Ɤ\U000105c0.json", "---json")]
    public void SlugifyMatchesPython(string value, string expected)
        => ConfigIdentity.Slugify(value).Should().Be(expected);

    [Fact]
    public void SlugifyReplacesUnpairedSurrogate()
        => ConfigIdentity.Slugify("a\ud800b").Should().Be("a-b");

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
    public void ConfigIdHashesWhenRelativePathSlugsToNothing()
    {
        var match = Match("json", "json");
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(" "u8))[..12];

        ConfigIdentity.NormaliseConfigId(match, " ").Should().Be($"json#{digest}");
    }

    [Fact]
    public void DisplayNamePrefersOriginalFilename()
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["config_original_filename"] = "  web.config " };

        ConfigIdentity.DisplayName(metadata, "app/web.config").Should().Be("web.config");
        ConfigIdentity.DisplayName(new Dictionary<string, object?>(StringComparer.Ordinal) { ["config_original_filename"] = " " }, "app/web.config").Should().Be("app/web.config");
        ConfigIdentity.DisplayName(null, "app/web.config").Should().Be("app/web.config");
    }

    [Fact]
    public void DisambiguateAppendsRootThenCounter()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "a/b" };
        ConfigIdentity.Disambiguate("c/d", 3, taken.Contains).Should().Be("c/d");
        ConfigIdentity.Disambiguate("a/b", 1, taken.Contains).Should().Be("a/b@root1");
        taken.Add("a/b@root1");
        ConfigIdentity.Disambiguate("a/b", 1, taken.Contains).Should().Be("a/b@root1.2");
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
    [InlineData("\uFEFF{\"signature\": \"7\"}")]
    [InlineData("{\"signature\": 7}")]
    public void CacheLoadIgnoresUnusableEntries(string content)
    {
        var cache = new DiffCache(Path.Combine(_tmp.FullName, "cache"));
        File.WriteAllText(cache.EntryPath("host", "cfg"), content);

        cache.Load("host", "cfg", "7").Should().BeNull();
    }

    // DiffCache.load calls payload.get("signature") on whatever json.loads returned.
    [Theory]
    [InlineData("[1, 2]", "'list' object has no attribute 'get'")]
    [InlineData("\"s\"", "'str' object has no attribute 'get'")]
    [InlineData("3", "'int' object has no attribute 'get'")]
    [InlineData("99999999999999999999", "'int' object has no attribute 'get'")]
    [InlineData("3.5", "'float' object has no attribute 'get'")]
    [InlineData("true", "'bool' object has no attribute 'get'")]
    [InlineData("null", "'NoneType' object has no attribute 'get'")]
    public void CacheLoadRaisesPythonsAttributeErrorForJsonThatIsNotAnObject(string content, string message)
    {
        var cache = new DiffCache(Path.Combine(_tmp.FullName, "cache"));
        File.WriteAllText(cache.EntryPath("host", "cfg"), content);

        var load = () => cache.Load("host", "cfg", "7");

        load.Should().Throw<InvalidDataException>().Which.Message.Should().Be(message);
    }

    [Fact]
    public void CacheLoadRaisesPythonsDecodeErrorForInvalidUtf8()
    {
        var cache = new DiffCache(Path.Combine(_tmp.FullName, "cache"));
        File.WriteAllBytes(cache.EntryPath("host", "cfg"), [0x7B, 0xFF, 0x7D]);

        var load = () => cache.Load("host", "cfg", "sig");

        load.Should().Throw<InvalidDataException>().Which.Message.Should().Be("'utf-8' codec can't decode byte 0xff in position 1: invalid start byte");
    }

    [Fact]
    public void CacheLoadOfADirectoryRaisesIsADirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "errno text is the Linux C library's");
        var cache = new DiffCache(Path.Combine(_tmp.FullName, "cache"));
        var entry = cache.EntryPath("host", "cfg");
        Directory.CreateDirectory(entry);

        var load = () => cache.Load("host", "cfg", "sig");
        var save = () => cache.Save("host", "cfg", "sig", new OrderedDictionary<string, object?>(StringComparer.Ordinal), TestContext.Current.CancellationToken);

        load.Should().Throw<IOException>().Which.Message.Should().Be($"[Errno 21] Is a directory: '{entry}'");
        save.Should().Throw<IOException>().Which.Message.Should().Be($"[Errno 21] Is a directory: '{entry}'");
    }

    [Fact]
    public void ACacheEntryThatIsADanglingLinkLoadsAsMissingAndIsWrittenThroughTheLink()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "symlinks are created without privileges only on posix");
        var cache = new DiffCache(Path.Combine(_tmp.FullName, "cache"));
        var entry = cache.EntryPath("host", "cfg");
        File.CreateSymbolicLink(entry, "gone.json");
        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["canonical"] = "x" };

        cache.Load("host", "cfg", "sig").Should().BeNull();
        cache.Save("host", "cfg", "sig", payload, TestContext.Current.CancellationToken);

        new FileInfo(entry).LinkTarget.Should().Be("gone.json");
        File.ReadAllText(Path.Combine(cache.Root, "gone.json")).Should().Be("{\"canonical\": \"x\", \"signature\": \"sig\"}");
        cache.Load("host", "cfg", "sig")!["canonical"].Should().Be("x");
        Directory.GetFiles(cache.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal).Should().Equal(Path.GetFileName(entry), "gone.json");
    }

    [Fact]
    public void ACacheEntryLinkIntoAMissingDirectoryRaisesNoSuchFile()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "symlinks are created without privileges only on posix");
        var cache = new DiffCache(Path.Combine(_tmp.FullName, "cache"));
        var entry = cache.EntryPath("host", "cfg");
        File.CreateSymbolicLink(entry, "missing/gone.json");

        var save = () => cache.Save("host", "cfg", "sig", new OrderedDictionary<string, object?>(StringComparer.Ordinal), TestContext.Current.CancellationToken);

        save.Should().Throw<IOException>().Which.Message.Should().Be($"[Errno 2] No such file or directory: '{entry}'");
        Directory.GetFiles(cache.Root).Select(Path.GetFileName).Should().Equal(Path.GetFileName(entry));
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

    // Expected values from CPython 3.13 multi_server._build_plans over the same request.
    [Fact]
    public void BuildPlansCoercesAsPlanFromMapping()
    {
        const string request = """
            {"plans": [
             {"host_id": 1.5e16, "label": ["a", {"b": null}], "roots": {"fixtures/x": 1, "  ": 2}, "baseline": {"is_preferred": [], "priority": " 4_2 "}, "throttle_seconds": " nan "},
             {"host_id": ["h", 2], "label": false, "roots": ["/srv/app", 0, null, [], "   ", ["~/r"]], "baseline": {"is_preferred": "0", "priority": "٤٢"}, "throttle_seconds": "-inf", "scope": 7, "cached_at": [1]},
             {"host_id": true, "label": "  ", "roots": "ab", "baseline": {"is_preferred": "false", "priority": 42.9}, "throttle_seconds": " 1_0e-4 ", "export": {}},
             {"host_id": "s4", "label": 0, "roots": ["/data"], "baseline": {"is_preferred": {"x": 1}, "priority": true}, "throttle_seconds": [0.5]},
             null, 7, "s", [],
             {"host_id": "s5", "roots": [" /srv/a "], "baseline": 0, "export": [], "throttle_seconds": 123456789012345678901234567890}
            ]}
            """;
        PythonJson.TryLoads(request, out var decoded).Should().BeTrue();

        var plans = MultiServerPlan.BuildPlans(decoded);

        plans.Select(plan => plan.HostId).Should().Equal("1.5e+16", "['h', 2]", "True", "s4", "s5");
        plans.Select(plan => plan.Label).Should().Equal("['a', {'b': None}]", "['h', 2]", "True", "s4", "s5");
        plans.Select(plan => string.Join('|', plan.Roots)).Should().Equal("fixtures/x", "/srv/app|['~/r']", "a|b", "/data", "/srv/a");
        plans.Select(plan => plan.IsPreferred).Should().Equal(false, true, true, true, false);
        plans.Select(plan => (int)plan.Priority).Should().Equal(42, 42, 42, 1, 0);
        plans.Select(plan => plan.ThrottleSeconds is { } value ? PythonRepr.Float(value) : "None").Should().Equal("nan", "-inf", "0.001", "None", "1.2345678901234568e+29");
    }

    [Theory]
    [InlineData("""{"plans": {"a": 1}}""", "InvalidDataException", "'plans' must be an array")]
    [InlineData("""[]""", "InvalidDataException", "'list' object has no attribute 'get'")]
    [InlineData("""{"plans": [{"roots": 5}]}""", "PythonTypeException", "'int' object is not iterable")]
    [InlineData("""{"plans": [{"roots": true, "baseline": {"priority": "high"}}]}""", "PythonTypeException", "'bool' object is not iterable")]
    [InlineData("""{"plans": [{"baseline": {"priority": "4.5"}}]}""", "PythonValueException", "invalid literal for int() with base 10: '4.5'")]
    [InlineData("""{"plans": [{"baseline": {"priority": null}}]}""", "PythonTypeException", "int() argument must be a string, a bytes-like object or a real number, not 'NoneType'")]
    [InlineData("""{"plans": [{"baseline": "yes"}]}""", "InvalidDataException", "'str' object has no attribute 'get'")]
    [InlineData("""{"plans": [{"export": [1]}]}""", "InvalidDataException", "'list' object has no attribute 'get'")]
    [InlineData("""{"plans": [{"roots": ["/srv/a", "~a\u0000b/x"]}]}""", "PythonValueException", "embedded null byte")]
    [InlineData("""{"plans": [{"throttle_seconds": 1e999}], "x": 1}""", "", "")]
    public void BuildPlansRaisesPythonsErrors(string request, string exceptionType, string message)
    {
        PythonJson.TryLoads(request, out var decoded).Should().BeTrue();

        var build = () => MultiServerPlan.BuildPlans(decoded);

        if (exceptionType.Length == 0)
        {
            build.Should().NotThrow().Which.Should().ContainSingle().Which.ThrottleSeconds.Should().Be(double.PositiveInfinity);
            return;
        }

        var raised = build.Should().Throw<Exception>().Which;
        raised.GetType().Name.Should().Be(exceptionType);
        raised.Message.Should().Be(message);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"plans": null}""")]
    [InlineData("""{"plans": 0}""")]
    [InlineData("""{"plans": "server01"}""")]
    [InlineData("""{"plans": [null, 7, "s", [], false]}""")]
    public void BuildPlansYieldsNoPlanWithoutAMappingEntry(string request)
    {
        PythonJson.TryLoads(request, out var decoded).Should().BeTrue();

        MultiServerPlan.BuildPlans(decoded).Should().BeEmpty();
    }

    [Fact]
    public void BaselineComparesPrioritiesPastThe64BitRange()
    {
        var huge = System.Numerics.BigInteger.Parse("123456789012345678901234567890", CultureInfo.InvariantCulture);
        BaselineSelector.Select([Plan("a", priority: int.MaxValue), Plan("b") with { Priority = huge }, Plan("c") with { Priority = -huge }]).HostId.Should().Be("b");
    }

    [Fact]
    public void TruncateCodePointsKeepsSurrogatePairsWhole()
    {
        MultiServerRunner.TruncateCodePoints("ab😀cd", 3).Should().Be("ab😀");
        MultiServerRunner.TruncateCodePoints("abc", 160).Should().Be("abc");
    }

    [Fact]
    public void ReadTextTranslatesNewlinesAndReplacesInvalidBytes()
    {
        var path = Path.Combine(_tmp.FullName, "raw.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'a', 0x0D, 0x0A, (byte)'b', 0x0D, 0xFF, (byte)'c']);

        MultiServerRunner.ReadText(path).Should().Be("﻿a\nb\n�c");
    }

    [Fact]
    public void ReadTextRefusesAFileLongerThanTheLimitWithoutReadingIt()
    {
        var path = Path.Combine(_tmp.FullName, "long.txt");
        File.WriteAllText(path, "12345");

        MultiServerRunner.ReadText(path, maxBytes: 5).Should().Be("12345");
        var read = () => MultiServerRunner.ReadText(path, maxBytes: 4);
        read.Should().Throw<IOException>().WithMessage("File is larger than the 4 bytes the scan reads whole: *");
        MultiServerRunner.DefaultMaxTextBytes.Should().Be(0x3FFFFFDF / 6, "a sixth of the longest runtime string: the text read fits one, with room for the canonical form and cache JSON of ordinary text");
    }
}
