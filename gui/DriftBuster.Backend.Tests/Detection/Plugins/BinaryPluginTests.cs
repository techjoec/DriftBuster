using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>Mirror of tests/formats/test_binary_plugin.py; expected values were read from the Python plugin.</summary>
public sealed class BinaryPluginTests
{
    private static string Fixture(string name) => RepoPaths.Fixtures("binary", name);

    internal static List<string> Strings(object? value) => ((IEnumerable<object?>)value!).Select(item => (string)item!).ToList();

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
        match.Metadata!["signature"].Should().Be("sqlite-format-3");
        match.Reasons[0].Should().Contain("SQLite database");

        match.Variant.Should().Be("generic");
        match.Confidence.Should().BeApproximately(0.98, 1e-9);
        match.Reasons.Should().Equal(
            "Detected SQLite database header (SQLite format 3)",
            "Enumerated 1 table(s) via sqlite3 pragma");
        match.Metadata.Keys.Should().Equal("signature", "table_count", "catalog_hint");
        match.Metadata["table_count"].Should().Be(1);
        match.Metadata["catalog_hint"].Should().Be("sqlite");
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
        match.Metadata!["signature"].Should().Be("bplist00");

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
        match.Metadata["has_body"].Should().Be(true);
    }

    [Fact]
    public void ReturnsNoneForUnmatchedPayload()
    {
        var plugin = new BinaryHybridPlugin();
        var match = plugin.Detect("binary.dat", [0x00, 0x01, 0x02], null);
        match.Should().BeNull();
    }

    // Python derives the text itself when none is passed and the bytes look like text.
    [Fact]
    public void FrontMatterIsReadFromTheSampleWhenNoTextIsPassed()
    {
        var plugin = new BinaryHybridPlugin();
        var path = Fixture("config_frontmatter.md");
        var match = plugin.Detect(path, File.ReadAllBytes(path), null);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("markdown-config");
        Strings(match.Metadata!["front_matter_keys"]).Should().Equal("environment", "retention_hours", "title");

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("---\nk: v\n---\n")).ToArray();
        var decoded = plugin.Detect("f.md", utf16, null);
        decoded.Should().NotBeNull();
        Strings(decoded!.Metadata!["front_matter_keys"]).Should().Equal("k");
        decoded.Metadata["has_body"].Should().Be(false);

        plugin.Detect("f.md", [], string.Empty).Should().BeNull();
    }

    public static TheoryData<string, string[], bool, string[]> FrontMatterCases() => new()
    {
        { "---\ntitle: x\n---\n", ["title"], false, ["Detected YAML front matter fenced with '---' markers", "Extracted keys: title"] },
        { "---\n\n---\nbody", [], true, ["Detected YAML front matter fenced with '---' markers"] },
        { "---  \r\nkey: v\n---\n", ["key"], false, ["Detected YAML front matter fenced with '---' markers", "Extracted keys: key"] },
        { "---\nkey: v\r\n---\n", ["key"], false, ["Detected YAML front matter fenced with '---' markers", "Extracted keys: key"] },
        {
            "--- \nk1: a\nk2: b\nk3: c\nk4: d\nk5: e\nk6: f\n: nokey\n---\nbody\n",
            ["k1", "k2", "k3", "k4", "k5", "k6"],
            true,
            ["Detected YAML front matter fenced with '---' markers", "Extracted keys: k1, k2, k3, k4, k5"]
        },
        { "---\nno colon\n---\n\n  \n", [], false, ["Detected YAML front matter fenced with '---' markers"] },
        { "---\x1c\nk: v\n---\n", ["k"], false, ["Detected YAML front matter fenced with '---' markers", "Extracted keys: k"] },
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
        match.Metadata["has_body"].Should().Be(hasBody);
    }

    [Theory]
    [InlineData("---\nk: v\n---")]
    [InlineData("\ufeff---\nk: v\n---\n")]
    [InlineData("# title\n---\nk: v\n---\n")]
    public void FrontMatterMustOpenTheTextAndCloseWithANewline(string text)
    {
        new BinaryHybridPlugin().Detect("f.md", Encoding.UTF8.GetBytes(text), text).Should().BeNull();
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

    // Spans are Python's own: re.match(r"^---\s*\n(?P<block>.*?\n)---\s*\n", text, re.DOTALL) start('block'),
    // end('block') and end(). The opening \s* is tried longest first and the block is lazy.
    [Theory]
    [InlineData("---\n\n\n---x\n---\n\n body", 6, 11, 16)]
    [InlineData("---\n\n---\n", 4, 5, 9)]
    [InlineData("---\n \n---\n", 4, 6, 10)]
    [InlineData("--- \n \n\n---  \n\n  x", 7, 8, 15)]
    [InlineData("---\n---\n---\n", 4, 8, 12)]
    [InlineData("---\na\n---\n---\n\n", 4, 6, 10)]
    public void FrontMatterSpansMatchThePythonRegex(string text, int blockStart, int blockEnd, int matchEnd)
    {
        BinaryHybridPlugin.TryMatchFrontMatter(text, out var start, out var end, out var stop).Should().BeTrue();
        (start, end, stop).Should().Be((blockStart, blockEnd, matchEnd));
    }

    // The regex backtracks the opening \s* over every newline and rescans the rest of the text from each: quadratic,
    // and past the old 2 s match timeout at 8000 blank lines. The hand matcher answers in one pass.
    [Theory]
    [InlineData(8000)]
    [InlineData(120000)]
    public void AnOpenFenceOverBlankLinesWithNoCloserIsLinear(int blankLines)
    {
        var text = "---\n" + new string('\n', blankLines);
        var started = System.Diagnostics.Stopwatch.StartNew();
        new BinaryHybridPlugin().Detect("f.md", Encoding.UTF8.GetBytes(text), text).Should().BeNull();
        BinaryHybridPlugin.TryMatchFrontMatter(text + "---\n", out _, out _, out var end).Should().BeTrue();
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        end.Should().Be(text.Length + 4);
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
        var error = (OrderedDictionary<string, object?>)match.Metadata["decode_error"]!;
        error.Keys.Should().Equal("type", "message");
        error["type"].Should().Be("InvalidFileException");
        error["message"].Should().Be("Invalid file");

        var headerOnly = plugin.Detect("x.plist", "bplist00"u8.ToArray(), null);
        ((OrderedDictionary<string, object?>)headerOnly!.Metadata!["decode_error"]!)["type"].Should().Be("InvalidFileException");
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
        match.Metadata["catalog_hint"].Should().Be("sqlite");

        var noSuffix = plugin.Detect("nope", "SQLite format 3\0"u8.ToArray(), null);
        noSuffix!.Metadata!["catalog_hint"].Should().Be("");
    }

    [Fact]
    public void SqliteTableCountIsNullWhenTheRealFileIsNotADatabase()
    {
        var directory = Directory.CreateTempSubdirectory("driftbuster-binary-");
        try
        {
            var path = Path.Combine(directory.FullName, "garbage.db");
            File.WriteAllBytes(path, "SQLite format 3\0"u8.ToArray().Concat(Enumerable.Repeat((byte)0xAB, 200)).ToArray());
            BinaryHybridPlugin.CountSqliteTables(path).Should().BeNull();
            BinaryHybridPlugin.CountSqliteTables(Fixture("settings.sqlite")).Should().Be(1);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // Python closes each connection; an unpooled connection must release the file as soon as it is disposed, so a
    // scanned database is neither held open (Linux) nor locked (Windows) after detection.
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

    // The sample is checked before the text: a plist is a plist whatever text the caller passed.
    [Fact]
    public void SampleBytesTakePrecedenceOverText()
    {
        var sample = File.ReadAllBytes(Fixture("preferences.plist"));
        var match = new BinaryHybridPlugin().Detect("x.md", sample, "---\nk: v\n---\n");
        match!.FormatName.Should().Be("plist");
    }
}
