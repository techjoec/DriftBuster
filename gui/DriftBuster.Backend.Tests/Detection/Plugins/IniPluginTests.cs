using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The ini plugin.</summary>
public sealed class IniPluginTests
{
    private static readonly byte[] BomUtf8 = [0xEF, 0xBB, 0xBF];

    private static string DotenvExportSample()
        => File.ReadAllText(RepoPaths.Fixtures("config", "dotenv.sanitised.env"), Encoding.UTF8).Trim();

    private const string JavaPropertiesColonSample =
        "spring.datasource.url: jdbc:postgresql://localhost/example\nmanagement.endpoints.enabled=true\nmultiline.value = first line \\\n second line";

    private const string DirectiveConfSample = "Include conf.d/*.conf\nOption ForceCommand internal-sftp\nAlias /static/ \"/var/www/static\"";

    private const string HybridIniJsonSample = "[general]\nenabled=true\n{\n    \"extra\": true\n}";

    private const string MixedNewlineSample = "[mix]\r\nfirst=1\nsecond=2\r\nthird=3\n";

    private static DetectionMatch? Detect(IniPlugin plugin, string filename, string content, byte[]? raw = null)
    {
        byte[] data;
        string text;
        if (raw is null)
        {
            data = Encoding.UTF8.GetBytes(content);
            text = content;
        }
        else
        {
            data = raw;
            text = FormatRegistry.DecodeText(raw).Text;
        }

        return plugin.Detect(filename, data, text);
    }

    private static DetectionMatch? Detect(string filename, string content, byte[]? raw = null) => Detect(new IniPlugin(), filename, content, raw);

    private static OrderedDictionary<string, object?> Mapping(object? value)
        => value.Should().BeOfType<OrderedDictionary<string, object?>>().Subject;

    private static List<OrderedDictionary<string, object?>> Entries(object? value)
        => value.Should().BeOfType<List<OrderedDictionary<string, object?>>>().Subject;

    private static List<string> Strings(object? value) => value.Should().BeOfType<List<string>>().Subject;

    [Fact]
    public void IniPluginDetectsSectionsAndKeys()
    {
        var match = Detect("settings.ini", "[general]\nenabled = true\nthreshold = 10\n[logging]\nlevel = info\n; trailing comment");

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("ini");
        match.Variant.Should().Be("sectioned-ini");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["section_count"].Should().Be(2);
        match.Metadata["key_value_pairs"].Should().BeOfType<int>().Which.Should().BeGreaterThanOrEqualTo(3);
        Mapping(match.Metadata["encoding_info"])["codec"].Should().Be("utf-8");
        match.Metadata["encoding"].Should().Be("utf-8");
        var commentStyle = Mapping(match.Metadata["comment_style"]);
        Strings(commentStyle["markers"]).Should().Equal(";");
        commentStyle["supports_inline_comments"].Should().Be(false);
        commentStyle["uses_export_prefix"].Should().Be(false);

        match.Confidence.Should().BeApproximately(0.9, 1e-9);
        match.Reasons.Should().Equal(
            "Detected utf-8 codec",
            "Found [section] headers indicative of INI structure",
            "Detected key/value assignments typical of INI-style configuration",
            "File extension .ini suggests INI-like configuration",
            "Detected comment markers (;, #, !) used by INI variants",
            "Section headers confirm classic INI layout");
        match.Metadata.Keys.Should().Equal(
            "encoding_info", "encoding", "sections", "section_count", "key_value_pairs", "equals_separator_pairs",
            "comment_style", "key_density", "detector_lineage");
        match.Metadata["key_density"].Should().Be(0.6);
        var lineage = Mapping(match.Metadata["detector_lineage"]);
        lineage["signal_score"].Should().Be(5);
        Mapping(lineage["signals"]).Keys.Should().Equal(
            "section_count", "key_value_pairs", "directive_lines", "export_assignments", "comment_lines", "continuations",
            "has_sections", "has_directives", "has_export_prefix", "dotenv_hint", "extension_hint");
    }

    [Fact]
    public void IniPluginDetectsDesktopIniVariant()
    {
        var match = Detect("desktop.ini", "[.ShellClassInfo]\n        IconResource=shell32.dll,3\n        ConfirmFileOp=0");

        match.Should().NotBeNull();
        match!.Variant.Should().Be("desktop-ini");
        match.Metadata.Should().NotBeNull();
        Strings(match.Metadata!["sections"]).Should().Contain(".ShellClassInfo");
        match.Metadata["profile_hint"].Should().Be("desktop.ini");
        match.Confidence.Should().BeApproximately(0.85, 1e-9);
        match.Metadata["key_density"].Should().Be(0.667);
        match.Reasons.Should().EndWith(new[] { "Section headers confirm classic INI layout", "Recognized Windows desktop.ini profile file name" });
    }

    [Fact]
    public void IniPluginClassifiesEnvFiles()
    {
        var match = Detect(".env", DotenvExportSample());

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("env-file");
        match.Variant.Should().Be("dotenv");
        match.Metadata.Should().NotBeNull();
        var commentStyle = Mapping(match.Metadata!["comment_style"]);
        commentStyle["uses_export_prefix"].Should().Be(true);
        match.Metadata["export_assignments"].Should().Be(1);
        match.Reasons.Should().Contain(reason => reason.Contains("dotenv", StringComparison.OrdinalIgnoreCase));
        var remediations = Entries(match.Metadata["remediations"]);
        remediations.Should().Contain(entry =>
            Equals(entry["id"], "env-sanitisation-workflow") && Equals(entry["documentation"], "scripts/fixtures/README.md"));
        remediations.Should().HaveCount(1);
        remediations[0].Keys.Should().Equal("id", "category", "summary", "documentation");
        remediations[0]["summary"].Should().Be("Sanitise dotenv fixtures via scripts/fixtures/README.md before sharing samples.");
        match.Confidence.Should().BeApproximately(0.7300000000000001, 1e-9);
    }

    [Fact]
    public void IniPluginPreservesJavaPropertiesClassification()
    {
        var match = Detect("application.properties", JavaPropertiesColonSample);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("ini");
        match.Variant.Should().Be("java-properties");
        match.Reasons.Should().Contain(reason => reason.Contains("java properties", StringComparison.OrdinalIgnoreCase));
        match.Metadata.Should().NotBeNull();
        match.Metadata!["colon_separator_pairs"].Should().Be(1);
        match.Metadata["continuations"].Should().Be(1);
        match.Metadata["equals_separator_pairs"].Should().Be(2);
        match.Confidence.Should().BeApproximately(0.65, 1e-9);
        match.Metadata.Should().NotContainKey("needs_review");
    }

    [Fact]
    public void IniPluginClassifiesUnixConfVariants()
    {
        var match = Detect(
            "httpd.conf",
            "LoadModule authz_core_module modules/mod_authz_core.so\n        ServerName example.com\n        <Directory \"/var/www/html\">\n            AllowOverride None\n        </Directory>");

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("unix-conf");
        match.Variant.Should().Be("apache-conf");
        match.Reasons.Should().Contain(reason => reason.Contains("apache", StringComparison.OrdinalIgnoreCase));
        match.Confidence.Should().BeApproximately(0.6, 1e-9);
        match.Metadata!["key_density"].Should().Be(0.0);
        match.Metadata["directive_line_count"].Should().Be(1);
    }

    [Fact]
    public void IniPluginClassifiesSectionlessIniVariant()
    {
        var match = Detect("plain.ini", "setting: true\npath: /etc/example");

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("ini");
        match.Variant.Should().Be("sectionless-ini");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["colon_separator_pairs"].Should().Be(2);
        match.Reasons.Should().Contain(reason => reason.Contains("sectionless", StringComparison.OrdinalIgnoreCase));
        Strings(match.Metadata["review_reasons"]).Should().Equal("Colon-only assignments outside .properties context");
        match.Confidence.Should().BeApproximately(0.65, 1e-9);
    }

    [Fact]
    public void IniPluginDetectsIniJsonHybrids()
    {
        var match = Detect("hybrid.conf", HybridIniJsonSample);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("ini-json-hybrid");
        match.Variant.Should().Be("section-json-hybrid");
        match.Reasons.Should().Contain(reason => reason.Contains("hybrid", StringComparison.OrdinalIgnoreCase));
        match.Confidence.Should().BeApproximately(0.75, 1e-9);
        match.Metadata!["key_density"].Should().Be(0.2);
    }

    [Fact]
    public void IniPluginDoesNotFlagPlaceholderBracesAsHybrid()
    {
        var match = Detect("placeholders.ini", "[template]\npattern={value}\ninclude={% include %}\npath={HOME}/bin");

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("ini");
        match.Variant.Should().Be("sectioned-ini");
        match.Reasons.Should().OnlyContain(reason => !reason.Contains("hybrid", StringComparison.OrdinalIgnoreCase));
        match.Confidence.Should().BeApproximately(0.85, 1e-9);
    }

    [Fact]
    public void IniPluginClassifiesDirectiveConfVariant()
    {
        var match = Detect("ssh_config.conf", DirectiveConfSample);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("unix-conf");
        match.Variant.Should().Be("directive-conf");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["directive_line_count"].Should().Be(3);
        match.Reasons.Should().Equal(
            "Detected utf-8 codec",
            "Found directive keywords common in INI/conf files",
            "File extension .conf suggests INI-like configuration",
            "Directive-heavy configuration without sections classified as Unix-style conf");
        match.Confidence.Should().BeApproximately(0.6, 1e-9);
    }

    [Fact]
    public void IniPluginClassifiesNginxConfVariant()
    {
        var match = Detect(
            "nginx.conf",
            "include /etc/nginx/mime.types;\nserver {\n    listen 80;\n    location / {\n        proxy_pass http://app;\n    }\n}");

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("unix-conf");
        match.Variant.Should().Be("nginx-conf");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["directive_line_count"].Should().Be(1);
        match.Reasons.Should().Contain(reason => reason.Contains("nginx", StringComparison.OrdinalIgnoreCase));
        match.Reasons.Should().EndWith("Detected nginx-style server/location blocks");
    }

    [Fact]
    public void IniPluginRecordsBomAndSensitiveHints()
    {
        const string content = "[credentials]\n    db_password = hunter2 # rotate soon\n    api_token=deadbeef ; inline note\n    plain_key = value";
        var raw = BomUtf8.Concat(Encoding.UTF8.GetBytes(content)).ToArray();

        var match = Detect("secrets.ini", content, raw);

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        var encodingInfo = Mapping(match.Metadata!["encoding_info"]);
        encodingInfo["bom_present"].Should().Be(true);
        encodingInfo["codec"].Should().Be("utf-8-sig");
        var commentStyle = Mapping(match.Metadata["comment_style"]);
        Strings(commentStyle["markers"]).Should().BeEquivalentTo("#", ";");
        commentStyle["supports_inline_comments"].Should().Be(true);
        var sensitiveHints = Entries(match.Metadata["sensitive_key_hints"]);
        var hintPairs = sensitiveHints.Select(hint => ((string)hint["key"]!, (string)hint["keyword"]!)).ToHashSet();
        hintPairs.Should().Contain(("db_password", "password"));
        hintPairs.Should().Contain(("api_token", "token"));
        var classification = Mapping(match.Metadata["secret_classification"]);
        Mapping(classification["category_counts"])["credential"].Should().BeOfType<int>().Which.Should().BeGreaterThanOrEqualTo(1);
        var classificationEntries = Entries(classification["entries"]);
        classificationEntries.Should().Contain(entry => Equals(entry["key"], "db_password"));
        classificationEntries.Should().Contain(entry => Equals(entry["key"], "api_token"));
        var focusKeys = Strings(match.Metadata["security_focus_keys"]);
        focusKeys.Should().Contain(["api_token", "db_password"]);
        var remediations = Entries(match.Metadata["remediations"]);
        var credentialEntry = remediations.First(entry => Equals(entry["category"], "credential"));
        ((string)credentialEntry["summary"]!).Should().ContainEquivalentOf("rotate");

        match.Reasons.Should().Equal(
            "Detected utf-8-sig codec with BOM",
            "Found [section] headers indicative of INI structure",
            "Detected key/value assignments typical of INI-style configuration",
            "Found inline comment markers following assignments",
            "Sensitive key 'db_password' matched keyword 'password'",
            "Sensitive key 'api_token' matched keyword 'token'",
            "Sensitive key 'plain_key' matched keyword 'key'",
            "File extension .ini suggests INI-like configuration",
            "Section headers confirm classic INI layout");
        Strings(commentStyle["markers"]).Should().Equal("#", ";");
        Mapping(classification["category_counts"]).Keys.Should().Equal("credential", "key-material", "token");
        remediations.Select(entry => entry["id"]).Should().Equal("ini-credential-remediation", "ini-key-material-remediation", "ini-token-remediation");
        credentialEntry["summary"].Should().Be("Rotate or scrub credential values referenced in configuration (db_password)");
        Strings(credentialEntry["related_keys"]).Should().Equal("db_password");
        credentialEntry["hint_count"].Should().Be(1);
        remediations[1]["summary"].Should().Be("Rotate or scrub key material values referenced in configuration (plain_key)");
        focusKeys.Should().Equal("api_token", "db_password", "plain_key");
        match.Metadata["encoding"].Should().Be("utf-8-sig");
    }

    [Fact]
    public void IniPluginRejectsPlainText()
    {
        Detect("notes.txt", "This is just a paragraph of text without configuration cues.").Should().BeNull();
    }

    [Fact]
    public void IniPluginRejectsYamlWithColons()
    {
        Detect("config.yaml", "foo: bar\n        bar: baz").Should().BeNull();
    }

    [Fact]
    public void IniPluginReturnsNoneWithoutText()
    {
        new IniPlugin().Detect("empty.ini", [], null).Should().BeNull();
    }

    [Fact]
    public void IniPluginHandlesDuplicatesAndSensitiveKeys()
    {
        const string content = "[General]\npassword = hunter2\nclient-secret = value1\nclient-secret = another\n[general]\nempty=\n;\nkey=\ninline = value ; comment\nexport FLAG=true";

        var match = Detect("config.ini", content);

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["section_count"].Should().Be(1);
        var hints = Entries(match.Metadata["sensitive_key_hints"]);
        hints.Should().Contain(hint => string.Equals((string)hint["key"]!, "client-secret", StringComparison.OrdinalIgnoreCase));
        var commentStyle = Mapping(match.Metadata["comment_style"]);
        commentStyle["supports_inline_comments"].Should().Be(true);

        // Duplicate (key, keyword) pairs collapse; a key matching two keywords is listed twice.
        hints.Select(hint => ((string)hint["key"]!, (string)hint["keyword"]!)).Should().Equal(
            ("password", "password"), ("client-secret", "secret"), ("client-secret", "client-secret"), ("key", "key"));
        Strings(match.Metadata["sections"]).Should().Equal("General");
        Mapping(match.Metadata["detector_lineage"])["signal_score"].Should().Be(6);
        Mapping(Mapping(match.Metadata["detector_lineage"])["signals"])["section_count"].Should().Be(2);
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Metadata["export_assignments"].Should().Be(1);
    }

    [Fact]
    public void IniPluginReturnsNoneWhenStructureWeak()
    {
        var content = string.Join("\n", ["foo: bar", "alpha", "beta", "gamma", "delta"]);
        Detect("notes.txt", content).Should().BeNull();
    }

    [Fact]
    public void YamlExtensionsAreLeftToTheYamlPlugin()
    {
        Detect("d.yml", "Include a\nSetEnv b c\n").Should().BeNull();
        Detect("y.yml", "a=1\nb=2\n").Should().NotBeNull(); // env-style wins before the yaml guard
        // Two directives beside a section still take the directive branch, which yields to YAML.
        Detect("d2.yaml", "[s]\nInclude a\nSetEnv b c\n").Should().BeNull();
        // One directive with low density falls through to the sectioned branch, which has no YAML guard.
        Detect("y3.yaml", "[s]\nInclude a\nk=v\nj=w\nl=x\n")!.Variant.Should().Be("sectioned-ini");
    }

    // The Apache and nginx hint scanners skip each whitespace run once as well.
    [Theory]
    [InlineData(30000, "    \n")]
    public void WhitespaceRunsAreScannedInLinearTime(int lines, string line)
    {
        var prefix = string.Concat(Enumerable.Repeat(line, lines));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var plain = Detect("run.ini", prefix + "[s]\nk=v\n");
        var apache = Detect("httpd.conf", prefix + "LoadModule x y\nServerName a\nk=v\n");
        var nginx = Detect(
            "nginx.conf",
            prefix + "include /etc/nginx/mime.types;\nserver {\n    listen 80;\n    location / {\n        proxy_pass http://app;\n    }\n}");
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));

        plain!.Variant.Should().Be("sectioned-ini");
        plain.Confidence.Should().BeApproximately(0.825, 1e-9);
        apache!.Variant.Should().Be("apache-conf");
        apache.Confidence.Should().BeApproximately(0.683333, 1e-6);
        nginx!.Variant.Should().Be("nginx-conf");
        nginx.Confidence.Should().BeApproximately(0.6, 1e-9);
    }
}
