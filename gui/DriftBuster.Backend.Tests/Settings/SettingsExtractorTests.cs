using DriftBuster.Backend.Settings;

namespace DriftBuster.Backend.Tests.Settings;

/// <summary>Each format's settings as people read them, and the fallbacks when a file does not parse.</summary>
public sealed class SettingsExtractorTests
{
    private static Dictionary<string, string> Read(string format, string text, string plugin = "", string hash = "h")
    {
        var settings = SettingsExtractor.Extract(format, plugin, text, hash);
        return settings.Entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }

    [Fact]
    public void DotNetConfigUsesAppSettingsAndConnectionStringNames()
    {
        var settings = Read("structured-config-xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <appSettings>
                <add key="ServiceUrl" value="https://a" />
              </appSettings>
              <connectionStrings>
                <add name="Orders" connectionString="Server=x" providerName="System.Data.SqlClient" />
              </connectionStrings>
              <system.web><compilation debug="true" /><customErrors mode="Off" /></system.web>
              <servers><server>a</server><server>b</server></servers>
              <modules />
            </configuration>
            """);

        settings.Should().Contain("appSettings:ServiceUrl", "https://a")
            .And.Contain("connectionStrings:Orders", "Server=x")
            .And.Contain("connectionStrings:Orders@providerName", "System.Data.SqlClient")
            .And.Contain("system.web/compilation@debug", "true")
            .And.Contain("servers/server[1]", "a")
            .And.Contain("servers/server[2]", "b")
            .And.Contain("modules", "(present)");
    }

    [Fact]
    public void XmlNamesElementsByTheirKeyAttribute()
    {
        Read("xml", "<nlog><rules><logger name=\"*\" minlevel=\"Info\" /></rules></nlog>")
            .Should().Contain("rules/logger[*]@minlevel", "Info");
    }

    [Fact]
    public void JsonUsesDottedPathsAndToleratesComments()
    {
        var settings = Read("json", "\uFEFF{\n  // comment\n  \"Cache\": { \"Minutes\": 15 },\n  \"Hosts\": [\"a\", \"b\"],\n  \"Empty\": {},\n  \"On\": true\n}");

        settings.Should().Contain("Cache.Minutes", "15")
            .And.Contain("Hosts[1]", "a")
            .And.Contain("Hosts[2]", "b")
            .And.Contain("Empty", "{}")
            .And.Contain("On", "true");
    }

    [Fact]
    public void IniKeysCarryTheirSection()
    {
        var settings = Read("ini", "; comment\nexport TOP=1\n[Service]\nName = \"Svc\"\nLogs=C:\\Logs\\\n[Paths]\nData: D:\\x\nflag\n");

        settings.Should().Contain("TOP", "1")
            .And.Contain("[Service] Name", "Svc")
            .And.Contain("[Service] Logs", "C:\\Logs\\")
            .And.Contain("[Paths] Data", "D:\\x")
            .And.Contain("[Paths] flag", string.Empty);
    }

    [Fact]
    public void PropertiesLinesContinueAfterABackslash()
    {
        Read("properties", "path=a\\\n  b\nnext=c\n").Should().Contain("path", "ab").And.Contain("next", "c");
    }

    [Fact]
    public void YamlUsesDottedPathsAndDocumentPrefixes()
    {
        var settings = Read("yaml", "agent:\n  poll: 30\n  tags: [a, b]\n  empty: {}\n---\nsecond: yes\n");

        settings.Should().Contain("agent.poll", "30")
            .And.Contain("agent.tags[2]", "b")
            .And.Contain("agent.empty", "{}")
            .And.Contain("doc2.second", "yes");
    }

    [Fact]
    public void TomlReadsTablesArraysOfTablesAndMultiLineValues()
    {
        var settings = Read("toml", "title = \"x\" # note\n[server]\nport = 8080\n\"quoted.key\" = 'lit'\nlist = [\n  1,\n  2,\n]\n[[plugin]]\nname = \"a\"\n[[plugin]]\nname = \"b\"\ntext = \"\"\"\nline\"\"\"\n");

        settings.Should().Contain("title", "x")
            .And.Contain("server.port", "8080")
            .And.Contain("server.quoted.key", "lit")
            .And.Contain("plugin[1].name", "a")
            .And.Contain("plugin[2].name", "b")
            .And.Contain("plugin[2].text", "line");
        settings["server.list"].Should().Contain("1,");
    }

    [Fact]
    public void BlockFilesNestNamesByBraces()
    {
        var settings = Read("unix-conf", "# c\nworker_processes 4;\nhttp {\n  server {\n    listen 80;\n    server_name a b;\n  }\n}\nkey = v\nlong \\\n  line\n", plugin: "conf");

        settings.Should().Contain("worker_processes", "4")
            .And.Contain("http.server.listen", "80")
            .And.Contain("http.server.server_name", "a b")
            .And.Contain("key", "v")
            .And.Contain("long", "line");
    }

    [Fact]
    public void RepeatedKeysAreNumbered()
    {
        var settings = SettingsExtractor.Extract("dockerfile", "dockerfile", "RUN a\nRUN b\n", "h").Entries;

        settings.Select(entry => entry.Key).Should().Equal("RUN", "RUN #2");
    }

    [Fact]
    public void UnparsableStructuredFilesFallBackToLines()
    {
        SettingsExtractor.Extract("json", "json", "not = json", "h").Mode.Should().Be(SettingsMode.Lines);
        SettingsExtractor.Extract("xml", "xml", "<a>", "h").Mode.Should().Be(SettingsMode.Lines);
        SettingsExtractor.Extract("yaml", "yaml", "a: [", "h").Mode.Should().Be(SettingsMode.Lines);
        SettingsExtractor.Extract("toml", "toml", "no equals here", "h").Mode.Should().Be(SettingsMode.Lines);
    }

    [Fact]
    public void AFileTooLargeForATableIsAlsoComparedAsAWhole()
    {
        var text = "[" + string.Join(", ", Enumerable.Range(0, 20001)) + "]";

        var settings = SettingsExtractor.Extract("json", "json", text, "abc");

        settings.Truncated.Should().BeTrue();
        settings.Entries[^1].Should().Be(new SettingEntry(SettingsExtractor.RestOfFileKey, "sha256:abc"));
    }

    [Fact]
    public void BinaryFilesCompareByHash()
    {
        var settings = SettingsExtractor.Extract("embedded-sql-db", "binary-hybrid", string.Empty, "abc");

        settings.Mode.Should().Be(SettingsMode.Binary);
        settings.Entries.Should().ContainSingle().Which.Value.Should().Be("sha256:abc");
    }

    [Fact]
    public void PluginNameChoosesTheReaderForAnUncataloguedFormat()
    {
        Read("config", "{\"a\": 1}", plugin: "json").Should().Contain("a", "1");
        Read("registry-live", "token: x\n").Should().Contain("token", "x");
    }
}
