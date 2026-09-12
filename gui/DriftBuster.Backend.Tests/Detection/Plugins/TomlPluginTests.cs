using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>Mirror of tests/formats/test_toml_plugin.py; expected values were read from the Python plugin.</summary>
public sealed class TomlPluginTests
{
    internal static DetectionMatch? Detect(string name, string content)
        => new TomlPlugin().Detect(name, Encoding.UTF8.GetBytes(content), content);

    internal static OrderedDictionary<string, object?> Spacing(DetectionMatch match)
        => match.Metadata!["key_value_spacing"].Should().BeOfType<OrderedDictionary<string, object?>>().Subject;

    internal static void AssertOneSpaceProfile(DetectionMatch match)
    {
        var spacing = Spacing(match);
        spacing.Keys.Should().Equal("before", "allowed_before", "after", "allowed_after");
        spacing["before"].Should().Be(1);
        YamlPluginTests.Ints(spacing["allowed_before"]).Should().Equal(0, 1, 2);
        spacing["after"].Should().Be(1);
        YamlPluginTests.Ints(spacing["allowed_after"]).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void TomlGenericAndArrayOfTables()
    {
        const string generic = """

                title = "Example"
                [server]
                host = "127.0.0.1"
                ports = [8000, 8001]
                
            """;
        var first = Detect("config.toml", generic);
        first.Should().NotBeNull();
        first!.FormatName.Should().Be("toml");
        first.Variant.Should().Be("generic");
        first.Confidence.Should().BeApproximately(0.8200000000000001, 1e-9);
        first.Reasons.Should().Equal(
            "File extension .toml suggests TOML content",
            "Found [table] headers typical of TOML",
            "Detected key = value assignments",
            "Found quoted value assignments",
            "Found array value assignments");
        first.Metadata!.Keys.Should().Equal("key_value_spacing");
        AssertOneSpaceProfile(first);

        const string aot = """

                [[inputs.cpu]]
                percpu = true
                [[inputs.mem]]
                [[outputs.influxdb]]
                url = "http://localhost"
                
            """;
        var second = Detect("telegraf.toml", aot);
        second.Should().NotBeNull();
        second!.Variant.Should().Be("array-of-tables");
        second.Confidence.Should().BeApproximately(0.8700000000000001, 1e-9);
        second.Reasons.Should().Equal(
            "File extension .toml suggests TOML content",
            "Found [[array-of-tables]] declaration",
            "Detected key = value assignments",
            "Found quoted value assignments");
        AssertOneSpaceProfile(second);
    }

    [Fact]
    public void TomlSpacingMetadataAndInlineTableReason()
    {
        var content = """

                [tool.black]
                line-length = 88
                skip-string-normalization = true

                [tool.poetry.dependencies]
                python = "^3.11"

                [tool.poetry.group.dev.dependencies]
                pytest = { version = "^7.0", extras = ["cov"] }
                
            """.Trim();
        var match = Detect("pyproject.toml", content);
        match.Should().NotBeNull();
        match!.Reasons.Should().Contain(reason => reason.Contains("inline table", StringComparison.Ordinal));
        var spacing = Spacing(match);
        spacing["after"].Should().NotBeNull();
        YamlPluginTests.Ints(spacing["allowed_after"]).Should().NotBeEmpty();

        match.Confidence.Should().BeApproximately(0.8500000000000001, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .toml suggests TOML content",
            "Found [table] headers typical of TOML",
            "Detected key = value assignments",
            "Found quoted value assignments",
            "Found array value assignments",
            "Found inline table assignments");
        AssertOneSpaceProfile(match);
        match.Metadata!.Keys.Should().Equal("key_value_spacing");
    }

    // Adversarial cases beyond the Python suite; every expected value was read from the Python plugin.
    [Fact]
    public void GateNeedsTwoContentSignals()
    {
        Detect("one.toml", "a = 1\n").Should().BeNull();
        Detect("sp.toml", "a=1\nb =2\nc= 3\nd  =  4\n").Should().BeNull();
        var match = Detect("noext", "a = 1\nb = 'x'\n");
        match!.Confidence.Should().BeApproximately(0.6000000000000001, 1e-9);
        match.Reasons.Should().Equal("Detected key = value assignments", "Found quoted value assignments");
        AssertOneSpaceProfile(match);
    }

    // "\s*" after "=" may cross the line break, so "a =" followed by "[t]" is both a key = value pair and an array value.
    [Fact]
    public void AssignmentValueMayStartOnTheNextLine()
    {
        var match = Detect("eq.toml", "a =\n[t]\n");
        match!.Confidence.Should().BeApproximately(0.8200000000000001, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .toml suggests TOML content",
            "Found [table] headers typical of TOML",
            "Detected key = value assignments",
            "Found array value assignments");
        var spacing = Spacing(match);
        spacing["after"].Should().Be(0);
        YamlPluginTests.Ints(spacing["allowed_after"]).Should().Equal(0, 1);
        match.Metadata!.Keys.Should().Equal("key_value_spacing");

        Detect("eq2.toml", "a =\n\n").Should().BeNull();
        Detect("eq3.toml", "a = \n[t]\n")!.Confidence.Should().BeApproximately(0.8200000000000001, 1e-9);
    }

    // Inline tables cannot span lines (no DOTALL) while arrays can.
    [Fact]
    public void InlineTablesStayOnOneLineButArraysMaySpan()
    {
        var inline = Detect("inl.toml", "a = {\nb = 1 }\nc = 'x'\n");
        inline!.Reasons.Should().Equal(
            "File extension .toml suggests TOML content",
            "Detected key = value assignments",
            "Found quoted value assignments");
        inline.Confidence.Should().BeApproximately(0.7200000000000001, 1e-9);

        var array = Detect("arr.toml", "a = [\n1,\n]\n");
        array!.Reasons.Should().Equal(
            "File extension .toml suggests TOML content",
            "Detected key = value assignments",
            "Found array value assignments");
        YamlPluginTests.Strings(array.Metadata!["review_reasons"]).Should().Equal("Array with trailing comma before closing bracket");
    }

    [Fact]
    public void TableHeaderToleratesTrailingWhitespaceAndCarriageReturn()
    {
        var match = Detect("hdr.toml", "[t]  \r\nk = 'v'\r\n");
        match!.Reasons.Should().Contain("Found [table] headers typical of TOML");
        match.Confidence.Should().BeApproximately(0.8200000000000001, 1e-9);
        match.Metadata!.Keys.Should().Equal("key_value_spacing");
    }

    [Fact]
    public void NullTextReturnsNull()
    {
        new TomlPlugin().Detect("x.toml", [], null).Should().BeNull();
    }

    // Python re-scans the run from every line start (quadratic); the port drives the patterns from line starts once.
    [Theory]
    [InlineData(50000, "\n")]
    [InlineData(30000, "    \n")]
    public void WhitespaceRunsAreScannedInLinearTime(int lines, string line)
    {
        var text = string.Concat(Enumerable.Repeat(line, lines)) + "[server]\nhost = \"localhost\"\nports = [1, 2]\n";
        var started = System.Diagnostics.Stopwatch.StartNew();
        var match = Detect("run.toml", text);
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));

        match!.Variant.Should().Be("generic");
        match.Confidence.Should().BeApproximately(0.82, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .toml suggests TOML content",
            "Found [table] headers typical of TOML",
            "Detected key = value assignments",
            "Found quoted value assignments",
            "Found array value assignments");
        Spacing(match)["before"].Should().Be(1);
        Spacing(match)["after"].Should().Be(1);
    }
}
