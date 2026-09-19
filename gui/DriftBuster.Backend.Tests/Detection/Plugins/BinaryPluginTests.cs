using System.Text;
using System.Text.Json.Nodes;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The binary-hybrid plugin.</summary>
public sealed class BinaryPluginTests
{
    private static string Fixture(string name) => RepoPaths.Fixtures("binary", name);

    internal static List<string> Strings(JsonNode? value) => [.. value.Should().BeOfType<JsonArray>().Subject.Select(item => item!.GetValue<string>())];

    [Fact]
    public void DetectsSqliteDatabase()
    {
        var plugin = new BinaryHybridPlugin();
        var path = Fixture("settings.sqlite");
        var sample = File.ReadAllBytes(path);
        var match = plugin.Detect(path, sample, null);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("embedded-sql-db");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["signature"].ShouldBeJson("sqlite-format-3");
        match.Reasons[0].Should().Contain("SQLite database");

        match.Variant.Should().Be("generic");
        match.Confidence.Should().BeApproximately(0.98, 1e-9);
        match.Reasons.Should().Equal(
            "Detected SQLite database header (SQLite format 3)",
            "Enumerated 1 table(s) via sqlite3 pragma");
        match.Metadata.Keys.Should().Equal("signature", "table_count", "catalog_hint");
        match.Metadata["table_count"].ShouldBeJson(1);
        match.Metadata["catalog_hint"].ShouldBeJson("sqlite");
    }

    [Fact]
    public void DetectsBinaryPlist()
    {
        var plugin = new BinaryHybridPlugin();
        var path = Fixture("preferences.plist");
        var sample = File.ReadAllBytes(path);
        var match = plugin.Detect(path, sample, null);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("plist");
        match.Variant.Should().Be("xml-or-binary");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["signature"].ShouldBeJson("bplist00");

        match.Confidence.Should().BeApproximately(0.92, 1e-9);
        match.Reasons.Should().Equal(
            "Detected binary property list header (bplist00)",
            "Parsed binary property list via plistlib");
        match.Metadata.Keys.Should().Equal("signature", "top_level_keys");
        Strings(match.Metadata["top_level_keys"]).Should().Equal("Environment", "FeatureFlags", "ReviewedAt");
    }

    [Fact]
    public void DetectsMarkdownFrontMatter()
    {
        var plugin = new BinaryHybridPlugin();
        var path = Fixture("config_frontmatter.md");
        var sample = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(sample);
        var match = plugin.Detect(path, sample, text);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("markdown-config");
        match.Metadata.Should().NotBeNull();
        match.Metadata!.Keys.Should().Contain("front_matter_keys");
        Strings(match.Metadata["front_matter_keys"]).Should().Contain("environment");

        match.Variant.Should().Be("embedded-yaml-frontmatter");
        match.Confidence.Should().BeApproximately(0.8, 1e-9);
        match.Reasons.Should().Equal(
            "Detected YAML front matter fenced with '---' markers",
            "Extracted keys: environment, retention_hours, title");
        match.Metadata.Keys.Should().Equal("front_matter_keys", "has_body");
        Strings(match.Metadata["front_matter_keys"]).Should().Equal("environment", "retention_hours", "title");
        match.Metadata["has_body"].ShouldBeJson(true);
    }

    [Fact]
    public void ReturnsNoneForUnmatchedPayload()
    {
        var plugin = new BinaryHybridPlugin();
        var match = plugin.Detect("binary.dat", [0x00, 0x01, 0x02], null);
        match.Should().BeNull();
    }

    public static TheoryData<string, string[], bool, string[]> FrontMatterCases() => new()
    {
        { "---\ntitle: x\n---\n", ["title"], false, ["Detected YAML front matter fenced with '---' markers", "Extracted keys: title"] },
        {
            "--- \nk1: a\nk2: b\nk3: c\nk4: d\nk5: e\nk6: f\n: nokey\n---\nbody\n",
            ["k1", "k2", "k3", "k4", "k5", "k6"],
            true,
            ["Detected YAML front matter fenced with '---' markers", "Extracted keys: k1, k2, k3, k4, k5"]
        },
        { "---\nb: 1\na: 2\nb: 3\n---\nx", ["a", "b"], true, ["Detected YAML front matter fenced with '---' markers", "Extracted keys: a, b"] },
    };

    [Theory]
    [MemberData(nameof(FrontMatterCases))]
    public void FrontMatterKeysAndBody(string text, string[] keys, bool hasBody, string[] reasons)
    {
        var match = new BinaryHybridPlugin().Detect("f.md", Encoding.UTF8.GetBytes(text), text);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("markdown-config");
        match.Variant.Should().Be("embedded-yaml-frontmatter");
        match.Confidence.Should().BeApproximately(0.8, 1e-9);
        match.Reasons.Should().Equal(reasons);
        match.Metadata!.Keys.Should().Equal("front_matter_keys", "has_body");
        Strings(match.Metadata["front_matter_keys"]).Should().Equal(keys);
        match.Metadata["has_body"].ShouldBeJson(hasBody);
    }

    [Fact]
    public void FrontMatterScansLongBlankRunsInBoundedTime()
    {
        var text = "---\n" + string.Concat(Enumerable.Repeat("\n---  x\n", 20000)) + "k: v\n---\nbody";
        var started = System.Diagnostics.Stopwatch.StartNew();
        var match = new BinaryHybridPlugin().Detect("f.md", Encoding.UTF8.GetBytes(text), text);
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        match.Should().NotBeNull();
        Strings(match!.Metadata!["front_matter_keys"]).Should().Equal("k");
    }

    [Fact]
    public void TruncatedPlistRecordsTheDecodeError()
    {
        var plugin = new BinaryHybridPlugin();
        var sample = File.ReadAllBytes(Fixture("preferences.plist"))[..100];
        var match = plugin.Detect("x.plist", sample, null);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("plist");
        match.Variant.Should().Be("xml-or-binary");
        match.Confidence.Should().BeApproximately(0.92, 1e-9);
        match.Reasons.Should().Equal(
            "Detected binary property list header (bplist00)",
            "Binary plist payload could not be decoded; recorded error metadata");
        match.Metadata!.Keys.Should().Equal("signature", "decode_error");
        var error = match.Metadata.Object("decode_error")!;
        error.Keys.Should().Equal("type", "message");
        error["type"].ShouldBeJson("InvalidDataException");
        error["message"].ShouldBeJson("The binary property list is not valid.");

        var headerOnly = plugin.Detect("x.plist", "bplist00"u8.ToArray(), null);
        headerOnly!.Metadata.Object("decode_error")!.Text("type").Should().Be("InvalidDataException");
    }

    [Fact]
    public void SqliteHeaderWithoutADatabaseHasNoTableCount()
    {
        var plugin = new BinaryHybridPlugin();
        var sample = "SQLite format 3\0"u8.ToArray().Concat(new byte[100]).ToArray();
        var match = plugin.Detect("nope.sqlite", sample, null);
        match.Should().NotBeNull();
        match!.Reasons.Should().Equal("Detected SQLite database header (SQLite format 3)");
        match.Metadata!.Keys.Should().Equal("signature", "table_count", "catalog_hint");
        match.Metadata["table_count"].Should().BeNull();
        match.Metadata["catalog_hint"].ShouldBeJson("sqlite");

        var noSuffix = plugin.Detect("nope", "SQLite format 3\0"u8.ToArray(), null);
        noSuffix!.Metadata!["catalog_hint"].ShouldBeJson("");
    }

    // An unpooled connection releases the file as soon as it is disposed, so a scanned database is neither held open (Linux) nor
    // locked (Windows) after detection.
    [Fact]
    public void SqliteFilesAreClosedAfterCounting()
    {
        var directory = Directory.CreateTempSubdirectory("driftbuster-binary-");
        try
        {
            var path = Path.Combine(directory.FullName, "held%41.sqlite");
            File.Copy(Fixture("settings.sqlite"), path);

            BinaryHybridPlugin.CountSqliteTables(path).Should().Be(1);

            if (OperatingSystem.IsLinux())
            {
                var open = Directory.EnumerateFileSystemEntries("/proc/self/fd")
                    .Select(entry => new FileInfo(entry).LinkTarget)
                    .Where(target => target is not null && target.StartsWith(directory.FullName, StringComparison.Ordinal));
                open.Should().BeEmpty();
            }

            File.Delete(path);
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
