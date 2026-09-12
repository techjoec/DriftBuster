using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>Mirror of tests/formats/test_ini_plugin.py, plus checks pinning the Python regex semantics.</summary>
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
        match.Reasons.Should().Contain(reason => reason.Contains("Section headers", StringComparison.Ordinal));

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
    public void IniPluginDetectsInlineClosingJsonHybrid()
    {
        var match = Detect("hybrid_inline.conf", "[service]\ndata = {\n    \"foo\": \"bar\",\n    \"baz\": \"qux\"}");

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("ini-json-hybrid");
        match.Variant.Should().Be("section-json-hybrid");
        match.Reasons.Should().Contain(reason => reason.Contains("hybrid", StringComparison.OrdinalIgnoreCase));
        match.Confidence.Should().BeApproximately(0.7625000000000001, 1e-9);
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
        match.Reasons.Should().Contain(reason => reason.Contains("directive", StringComparison.OrdinalIgnoreCase));
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
        match.Reasons.Should().Contain(reason => reason.Contains("Sensitive key", StringComparison.Ordinal));
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
        match.Reasons.Should().Contain(reason => reason.Contains("utf-8-sig", StringComparison.Ordinal));

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
    public void IniPluginHandlesMixedNewlines()
    {
        var match = Detect("mixed.ini", MixedNewlineSample);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("ini");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["key_value_pairs"].Should().Be(3);
        match.Metadata["key_density"].Should().BeOfType<double>().Which.Should().NotBe(0.0);
        match.Metadata["key_density"].Should().Be(0.75);
        Strings(match.Metadata["sections"]).Should().Equal("mix");
    }

    [Fact]
    public void IniPluginReportsLatin1Encoding()
    {
        const string text = "[credenciales]\ncontrase\u00f1a=secreta\napi_key=abcd1234\n; nota=\u00f1";
        var raw = Encoding.Latin1.GetBytes(text);

        var match = Detect("credenciales.ini", text, raw);

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        var encodingInfo = Mapping(match.Metadata!["encoding_info"]);
        encodingInfo["codec"].Should().Be("latin-1");
        encodingInfo["bom_present"].Should().Be(false);
        match.Reasons.Should().Contain(reason => reason.Contains("latin-1", StringComparison.Ordinal));
        // The n-tilde puts the first key outside [A-Za-z0-9_.-], so only api_key counts, matching two keywords.
        match.Metadata["key_value_pairs"].Should().Be(1);
        match.Reasons.Should().Contain("Sensitive key 'api_key' matched keyword 'api-key'");
        match.Reasons.Should().Contain("Sensitive key 'api_key' matched keyword 'key'");
        match.Confidence.Should().BeApproximately(0.8333333333333334, 1e-9);
        match.Metadata["key_density"].Should().Be(0.333);
    }

    [Fact]
    public void IniPluginReportsDetectorLineage()
    {
        var match = Detect(".env.development", DotenvExportSample());

        match.Should().NotBeNull();
        var metadata = match!.Metadata;
        metadata.Should().NotBeNull();
        var lineage = Mapping(metadata!["detector_lineage"]);
        lineage["family"].Should().Be("ini-lineage");
        lineage["format"].Should().Be(match.FormatName);
        lineage["variant"].Should().Be(match.Variant);
        var signals = Mapping(lineage["signals"]);
        signals["key_value_pairs"].Should().BeOfType<int>().Which.Should().BeGreaterThanOrEqualTo(3);
        signals["has_sections"].Should().Be(false);
        lineage["signal_score"].Should().BeOfType<int>().Which.Should().BeGreaterThanOrEqualTo(2);
        lineage["signal_score"].Should().Be(5);
        signals["dotenv_hint"].Should().Be(true);
        signals["extension_hint"].Should().Be(false);
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
    public void IniPluginExtensionHintWithoutRegisteredExtension()
    {
        var plugin = new IniPlugin { IniExtensions = new HashSet<string>([".cfg", ".conf"], StringComparer.Ordinal) };
        var match = Detect(plugin, "settings.ini", "key=value");
        match.Should().NotBeNull();
        match!.Reasons.Should().Contain(reason => reason.Contains("suffix .ini", StringComparison.Ordinal));
    }

    [Fact]
    public void IniPluginReturnsNoneWhenStructureWeak()
    {
        var content = string.Join("\n", ["foo: bar", "alpha", "beta", "gamma", "delta"]);
        Detect("notes.txt", content).Should().BeNull();
    }

    [Fact]
    public void IniPluginRequiresMultipleSignals()
    {
        Detect("rules.txt", "Include conf.d/*.conf\n").Should().BeNull();
    }

    [Fact]
    public void IniPluginConfidenceBoostForManyPairs()
    {
        var lines = Enumerable.Range(0, 6).Select(i => $"key{i}=value{i}");
        var match = Detect("bulk.ini", string.Join("\n", lines));
        match.Should().NotBeNull();
        match!.Confidence.Should().BeGreaterThanOrEqualTo(0.7);
        match.Confidence.Should().BeApproximately(0.7000000000000001, 1e-9);
        match.FormatName.Should().Be("env-file");
    }

    [Fact]
    public void IniPluginDetectsBlockJsonAssignments()
    {
        const string content = "foo = value {\nbar = 1\nalso = { \"inner\": true }\n}\n[Next]\nsetting=true\njson = { \"key\": \"value\" }";

        var match = Detect("hybrid.ini", content);
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata.Should().ContainKey("section_count");
        match.FormatName.Should().Be("ini-json-hybrid");
        match.Confidence.Should().BeApproximately(0.85, 1e-9);
        match.Metadata!["key_density"].Should().Be(0.714);
    }

    [Fact]
    public void IniPluginBreaksBlockJsonOnSectionHeader()
    {
        // JSON block starts, then a new section header appears before a closing brace.
        const string content = "options = {\n\"a\": 1\n[Next]\nafterwards=true";

        var match = Detect("section-before-close.ini", content);
        match.Should().NotBeNull();
        // Should still be classified as INI due to key/value pairs and signals.
        match!.FormatName.Should().BeOneOf("ini", "ini-json-hybrid");
        match.FormatName.Should().Be("ini");
        match.Variant.Should().Be("sectioned-ini");
    }

    [Fact]
    public void IniPluginBreaksBlockJsonOnNestedAssignment()
    {
        // A nested assignment with `{` and `=` appears before the original JSON block closes.
        const string content = "data = {\n\"a\": 1\nnested = {\n    \"b\": 2\n}\nkey=value";

        var match = Detect("nested-json-before-close.ini", content);
        match.Should().NotBeNull();
        // Depending on signal balance, this may classify as INI or env-file.
        match!.FormatName.Should().BeOneOf("ini", "ini-json-hybrid", "env-file");
        match.FormatName.Should().Be("env-file");
        var remediations = Entries(match.Metadata!["remediations"]);
        remediations.Select(entry => entry["id"]).Should().Equal("ini-key-material-remediation", "env-sanitisation-workflow");
    }

    [Fact]
    public void IniPluginInlineCommentLoopSkipsEmptyValues()
    {
        // Ensure a key with an empty value is present to exercise the continue branch.
        const string content = "empty=\nkey=value ; comment";

        var match = Detect("empty-value.ini", content);
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        // Confirm inline comment detection still works overall.
        var style = Mapping(match.Metadata!["comment_style"]);
        style["supports_inline_comments"].Should().Be(true);
        // "empty=\nkey=..." : the lazy value of "empty" is empty, so only one assignment is found.
        match.Metadata["key_value_pairs"].Should().Be(1);
    }

    [Fact]
    public void IniPluginHandlesBlankCommentsAndInlineMarkers()
    {
        const string content = "# comment\n\nkey = value ; inline # extra";

        var match = Detect("markers.ini", content);
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        var commentStyle = Mapping(match.Metadata!["comment_style"]);
        Strings(commentStyle["markers"]).Should().BeEquivalentTo(";", "#");
        commentStyle["supports_inline_comments"].Should().Be(true);
        Strings(commentStyle["markers"]).Should().Equal("#", ";");
    }

    // Python regex semantics that .NET does not share, pinned against interpreter results.

    [Fact]
    public void DirectiveKeywordsFoldCaseLikePythonRe()
    {
        // U+0130 and U+0131 match 'i' under Python IGNORECASE; U+212A (KELVIN SIGN) matches 'k', so it is not an 'I'.
        var dotted = Detect("k.conf", "\u212Anclude foo\nInclude bar\n\u0130nclude baz\n\u0131nclude qux\n");
        dotted!.Metadata!["directive_line_count"].Should().Be(3);

        // U+017F (LONG S) matches 's', and the Apache hint folds the same way.
        var longS = Detect("s.conf", "\u017Fetenv a b\nSETENV c d\n");
        longS!.Metadata!["directive_line_count"].Should().Be(2);
        longS.Variant.Should().Be("apache-conf");
    }

    [Fact]
    public void ApacheWordBoundaryUsesPythonWordClass()
    {
        // U+00B2 is No (a Python word character, not a .NET one): no boundary, so no Apache hint.
        Detect("b1.conf", "LoadModule\u00b2 x\nInclude c\nOption d e\n")!.Variant.Should().Be("directive-conf");
        // U+0301 is Mn (a .NET word character, not a Python one): boundary, so the Apache hint fires.
        Detect("b2.conf", "ServerName\u0301 x\nInclude c\nOption d e\n")!.Variant.Should().Be("apache-conf");
        // An astral letter (U+1D400) is a Python word character.
        Detect("b3.conf", "ServerName\U0001D400 x\nInclude c\nOption d e\n")!.Variant.Should().Be("directive-conf");
        Detect("b4.conf", "loadmodule_x y\nInclude c\nOption d e\n")!.Variant.Should().Be("directive-conf");
        Detect("b5.conf", "<directory /x>\nInclude c\nOption d e\n")!.Variant.Should().Be("apache-conf");
        Detect("b6.conf", "ServerName x\nInclude c\nOption d e\n")!.Variant.Should().Be("apache-conf");
    }

    [Fact]
    public void NginxHintAllowsWhitespaceAcrossLines()
    {
        Detect("n.conf", "include x;\nSERVER\n{\nlisten 80;\n}\n")!.Variant.Should().Be("nginx-conf");
        Detect("n2.conf", "include x;\nupstream\tapp {\n}\n")!.Variant.Should().Be("nginx-conf");
        Detect("n3.conf", "include x;\nlocation\n/ {}\n")!.Variant.Should().Be("nginx-conf");
        Detect("n4.conf", "include x;\nlocationx / {}\n")!.Variant.Should().Be("directive-conf");
    }

    [Fact]
    public void UnitSeparatorIsWhitespaceForTheRegexes()
    {
        // U+001F survives splitlines and is \s in Python; U+001C is a line boundary and \s too.
        var match = Detect("u.ini", "\x1fkey=value\x1f\n[sec]\x1f\nk2=v2\n");
        Strings(match!.Metadata!["sections"]).Should().Equal("sec");
        match.Metadata["key_value_pairs"].Should().Be(2);
        var fs = Detect("u2.ini", "[sec]\x1c\nk2=v2\n");
        Strings(fs!.Metadata!["sections"]).Should().Equal("sec");
        var nbsp = Detect("n.ini", "\u00a0[s]\u00a0\nk\u00a0=\u00a0v\u00a0;\u00a0c\n");
        Strings(nbsp!.Metadata!["sections"]).Should().Equal("s");
        Mapping(nbsp.Metadata["comment_style"])["supports_inline_comments"].Should().Be(true);
    }

    [Fact]
    public void KeyDensityRoundsLikePython()
    {
        // 1/16 is a binary tie rounded to even; 1/8 is exact; 1/2000 is above the midpoint in binary.
        IniPlugin.PythonRound(1.0 / 16, 3).Should().Be(0.062);
        IniPlugin.PythonRound(3.0 / 16, 3).Should().Be(0.188);
        IniPlugin.PythonRound(0.0005, 3).Should().Be(0.001);
        IniPlugin.PythonRound(1.0 / 3, 3).Should().Be(0.333);
        IniPlugin.PythonRound(1.0, 3).Should().Be(1.0);
        IniPlugin.PythonRound(0.0, 3).Should().Be(0.0);
        IniPlugin.PythonRound(2.0 / 3, 3).Should().Be(0.667);
        Detect("d.ini", "k=1\n" + string.Join("\n", Enumerable.Range(0, 15).Select(i => $"line{i}")))!.Metadata!["key_density"].Should().Be(0.062);
        Detect("d3.ini", "k=1\n" + string.Join("\n", Enumerable.Range(0, 1999).Select(i => $"line{i}")))!.Metadata!["key_density"].Should().Be(0.001);
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

    [Fact]
    public void DesktopIniOverridesEnvStyle()
    {
        var match = Detect("desktop.ini", "a=1\nb=2\n");
        match!.FormatName.Should().Be("env-file");
        match.Variant.Should().Be("desktop-ini");
        Mapping(match.Metadata!["detector_lineage"])["variant"].Should().Be("desktop-ini");
    }

    // The commented-.properties fallback (signal score below 2 with ten commented pairs) is unreachable in Python
    // and in the port alike: the gate needs an assignment or a directive (+1) and the commented pairs are comment
    // lines (+1), so such a file always scores through the normal ladder.
    [Fact]
    public void CommentedPropertiesScoreThroughTheNormalLadder()
    {
        var ten = Detect("ex.properties", string.Join("\n", Enumerable.Range(0, 10).Select(i => $"# key{i}=value{i}")) + "\nreal=1\n");
        ten!.Variant.Should().Be("java-properties");
        ten.Confidence.Should().BeApproximately(0.7000000000000001, 1e-9);
        ten.Reasons.Should().NotContain("Found numerous commented key/value examples in .properties file");
        // A single colon assignment in a .preferences file passes the colon-only gate and scores 2 on density.
        var single = Detect("x.preferences", "a: 1\n");
        single!.Variant.Should().Be("sectionless-ini");
        single.Confidence.Should().BeApproximately(0.55, 1e-9);
    }

    // Python dedupes on str.lower(): U+0130 lowers to "i" + U+0307, so "[İ]" and "[i̇]" are one section.
    [Fact]
    public void SectionsDedupeWithPythonLower()
    {
        var match = Detect("sections-turkish.ini", "[\u0130]\n[i\u0307]\n[i]\nk=v\n");
        Strings(match!.Metadata!["sections"]).Should().Equal("\u0130", "i");
        match.Metadata["section_count"].Should().Be(2);
    }

    // A run of blank lines is scanned once, not once per line start: Python's ^\s* re-scans and backtracks the
    // run from every line start (quadratic), which is what the 2 s match timeout would otherwise hit.
    [Fact]
    public void BlankLineRunsAreMatchedInLinearTime()
    {
        var match = Detect("blank-lines-5000.ini", new string('\n', 5000) + "[s]\nk=v\n");
        match!.Variant.Should().Be("sectioned-ini");
        match.Confidence.Should().BeApproximately(0.825, 1e-9);
        match.Metadata!["key_value_pairs"].Should().Be(1);

        var sampleOfNewlines = new string('\n', 131072) + "[s]\nk: v\n";
        var started = System.Diagnostics.Stopwatch.StartNew();
        var full = Detect("blank-lines-sample.ini", sampleOfNewlines);
        var spaced = Detect("space-lines-sample.ini", string.Concat(Enumerable.Repeat("    \n", 30000)) + "export X=1\n");
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        full!.Variant.Should().Be("sectioned-ini");
        Strings(full.Metadata!["sections"]).Should().Equal("s");
        spaced!.Metadata!["export_assignments"].Should().Be(1);
    }

    [Fact]
    public void SectionsDedupeCaseInsensitivelyAndSnapshotTen()
    {
        var match = Detect("dupsec.ini", "[A]\n[a]\n[ A ]\n[b]\nk=v\n");
        Strings(match!.Metadata!["sections"]).Should().Equal("A", "b");
        match.Metadata["section_count"].Should().Be(2);
        Mapping(Mapping(match.Metadata["detector_lineage"])["signals"])["section_count"].Should().Be(4);

        var many = Detect("manysec.ini", string.Join("\n", Enumerable.Range(0, 12).Select(i => $"[s{i}]\nk{i}=v")));
        Strings(many!.Metadata!["sections"]).Should().HaveCount(10);
        many.Metadata["section_count"].Should().Be(12);
    }

    // The Apache and nginx hint scanners skip each whitespace run once as well.
    [Theory]
    [InlineData(50000, "\n")]
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
