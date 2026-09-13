using System.Security.Cryptography;
using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>
/// Mirror of tests/formats/test_xml_plugin.py, plus fixture checks pinning the Python provenance hashes and
/// confidence values. Test payloads keep the Python triple-quoted framing (leading newline, four-space indentation,
/// trailing indented line) because line and column numbers in the provenance depend on it.
/// </summary>
public sealed class XmlPluginTests
{
    private static DetectionMatch? Detect(string filename, string content)
        => new XmlPlugin().Detect(filename, Encoding.UTF8.GetBytes(content), content);

    /// <summary>A Python <c>"""..."""</c> block: newline, then every line indented four spaces, then an indented blank tail.</summary>
    internal static string Block(string body) => "\n    " + body.Replace("\n", "\n    ", StringComparison.Ordinal) + "\n    ";

    /// <summary>The same block after <c>.strip()</c>: the first line bare, the rest still indented.</summary>
    internal static string Stripped(string body) => body.Replace("\n", "\n    ", StringComparison.Ordinal);

    internal static OrderedDictionary<string, object?> Mapping(object? value)
        => value.Should().BeOfType<OrderedDictionary<string, object?>>().Subject;

    internal static List<OrderedDictionary<string, object?>> Entries(object? value)
        => value.Should().BeOfType<List<OrderedDictionary<string, object?>>>().Subject;

    internal static List<string> Strings(object? value) => value.Should().BeOfType<List<string>>().Subject;

    internal static void AssertMapping(object? value, params (string Key, object? Value)[] expected)
    {
        var mapping = Mapping(value);
        mapping.Keys.Should().Equal(expected.Select(pair => pair.Key));
        foreach (var (key, expectedValue) in expected)
        {
            mapping[key].Should().Be(expectedValue, "key {0}", key);
        }
    }

    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private const string XdtTransformContent = """
        <?xml version="1.0"?>
        <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
          <system.webServer>
            <modules>
              <add name="Example" xdt:Transform="Replace" />
            </modules>
          </system.webServer>
        </configuration>
        """;

    private const string PrefixedManifestContent = """
        <asm:assembly xmlns="urn:schemas-microsoft-com:asm.v1" xmlns:asm="urn:custom">
          <assemblyIdentity name="Prefixed" version="1.0.0.0" />
        </asm:assembly>
        """;

    private const string StartupContent = """
        <configuration>
          <startup>
            <supportedRuntime version="v4.0" />
          </startup>
        </configuration>
        """;

    [Fact]
    public void XmlPluginDetectsFrameworkConfig()
    {
        var content = Block("""
            <?xml version="1.0"?>
            <configuration>
              <system.web>
                <compilation debug="true" />
              </system.web>
            </configuration>
            """);
        var match = Detect("web.config", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("structured-config-xml");
        match.Variant.Should().Be("web-config");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["config_role"].Should().Be("web");

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Matched known configuration section tags used by web frameworks",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "Filename web.config strongly suggests web-hosted configuration");
        match.Metadata.Keys.Should().Equal("xml_declaration", "root_tag", "root_local_name", "config_original_filename", "config_role");
        AssertMapping(match.Metadata["xml_declaration"], ("version", "1.0"));
        match.Metadata["config_original_filename"].Should().Be("web.config");
    }

    [Fact]
    public void XmlPluginDetectsAppConfigVariant()
    {
        var content = Block("""
            <?xml version="1.0"?>
            <configuration>
              <startup>
                <supportedRuntime version="v4.0" />
              </startup>
            </configuration>
            """);
        var match = Detect("App.config", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("structured-config-xml");
        match.Variant.Should().Be("app-config");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["config_role"].Should().Be("app");
        match.Reasons.Should().Contain(reason => reason.Contains("app.config", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "Filename app.config indicates per-application configuration");
        match.Metadata.Keys.Should().Equal("xml_declaration", "root_tag", "root_local_name", "config_original_filename", "config_role");
        match.Metadata["config_original_filename"].Should().Be("App.config");
    }

    [Fact]
    public void XmlPluginDetectsMachineConfigVariant()
    {
        var content = Block("""
            <?xml version="1.0"?>
            <configuration>
              <system.web>
                <trust level="Full" />
              </system.web>
            </configuration>
            """);
        var match = Detect("machine.config", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("structured-config-xml");
        match.Variant.Should().Be("machine-config");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["config_role"].Should().Be("machine");
        match.Reasons.Should().Contain(reason => reason.Contains("machine.config", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Matched known configuration section tags used by web frameworks",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "Filename machine.config indicates machine-wide configuration");
        match.Metadata.Keys.Should().Equal("xml_declaration", "root_tag", "root_local_name", "config_original_filename", "config_role");
    }

    [Fact]
    public void XmlPluginDetectsConfigTransformScope()
    {
        var match = Detect("web.Release.config", Block(XdtTransformContent));

        match.Should().NotBeNull();
        match!.Variant.Should().Be("web-config-transform");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["config_transform"].Should().Be(true);
        match.Metadata["config_transform_scope"].Should().Be("web");
        Strings(match.Metadata["config_transform_stages"]).Should().Equal("Release");
        match.Metadata["config_transform_primary_stage"].Should().Be("Release");
        match.Metadata["config_transform_stage_count"].Should().Be(1);

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "Recorded XML namespace declarations (xdt\u2192http://schemas.microsoft.com/XML-Document-Transform @L3)",
            "Filename pattern web|app.*.config suggests a build-specific transform",
            "Detected XML-Document-Transform namespace declaration (xdt)",
            "Found xdt:Transform attribute indicating config transform instructions",
            "Filename stage 'Release' indicates transform precedence");
        match.Metadata.Keys.Should().Equal(
            "xml_declaration", "root_tag", "root_local_name", "root_attributes", "namespaces", "namespace_provenance",
            "config_original_filename", "config_transform", "config_transform_scope", "config_transform_stages",
            "config_transform_primary_stage", "config_transform_stage_count", "config_role");
        AssertMapping(
            Entries(match.Metadata["namespace_provenance"])[0],
            ("attribute", "xmlns:xdt"),
            ("prefix", "xdt"),
            ("uri", "http://schemas.microsoft.com/XML-Document-Transform"),
            ("line", 3),
            ("column", 20),
            ("source", "root-attribute"),
            ("hash", "66dd72c0f369"));
    }

    [Fact]
    public void XmlPluginRecordsMultiStageTransformMetadata()
    {
        var match = Detect("web.Release.QA.config", Block(XdtTransformContent));

        match.Should().NotBeNull();
        match!.Variant.Should().Be("web-config-transform");
        match.Metadata.Should().NotBeNull();
        Strings(match.Metadata!["config_transform_stages"]).Should().Equal("Release", "QA");
        match.Metadata["config_transform_primary_stage"].Should().Be("QA");
        match.Metadata["config_transform_stage_count"].Should().Be(2);
        match.Reasons.Should().Contain(reason => reason.Contains("Release -> QA", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "Recorded XML namespace declarations (xdt\u2192http://schemas.microsoft.com/XML-Document-Transform @L3)",
            "Filename pattern web|app.*.config suggests a build-specific transform",
            "Detected XML-Document-Transform namespace declaration (xdt)",
            "Found xdt:Transform attribute indicating config transform instructions",
            "Transform stages applied in order: Release -> QA");
        match.Metadata.Keys.Should().Equal(
            "xml_declaration", "root_tag", "root_local_name", "root_attributes", "namespaces", "namespace_provenance",
            "config_original_filename", "config_transform", "config_transform_scope", "config_transform_stages",
            "config_transform_primary_stage", "config_transform_stage_count", "config_role");
    }

    [Fact]
    public void XmlPluginDetectsAppConfigTransformVariant()
    {
        var content = Block("""
            <?xml version="1.0"?>
            <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
              <startup>
                <supportedRuntime xdt:Transform="Replace" />
              </startup>
            </configuration>
            """);
        var match = Detect("app.Release.config", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("structured-config-xml");
        match.Variant.Should().Be("app-config-transform");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["config_transform"].Should().Be(true);
        match.Metadata["config_transform_scope"].Should().Be("app");
        Strings(match.Metadata["config_transform_stages"]).Should().Equal("Release");
        match.Metadata["config_transform_stage_count"].Should().Be(1);

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "Recorded XML namespace declarations (xdt\u2192http://schemas.microsoft.com/XML-Document-Transform @L3)",
            "Filename pattern web|app.*.config suggests a build-specific transform",
            "Detected XML-Document-Transform namespace declaration (xdt)",
            "Found xdt:Transform attribute indicating config transform instructions",
            "Filename stage 'Release' indicates transform precedence");
        match.Metadata["config_role"].Should().Be("app");
    }

    [Fact]
    public void XmlPluginDetectsGenericConfigTransformVariant()
    {
        var content = Block("""
            <?xml version="1.0"?>
            <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
              <appSettings>
                <add key="Feature" value="true" />
              </appSettings>
            </configuration>
            """);
        var match = Detect("service.Stage.config", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("structured-config-xml");
        match.Variant.Should().Be("config-transform");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["config_transform"].Should().Be(true);
        match.Metadata.Should().NotContainKey("config_transform_scope");

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Matched known configuration section tags used by web frameworks",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "Recorded XML namespace declarations (xdt\u2192http://schemas.microsoft.com/XML-Document-Transform @L3)",
            "Captured feature flag attribute hints",
            "Detected XML-Document-Transform namespace declaration (xdt)");
        match.Metadata.Keys.Should().Equal(
            "xml_declaration", "root_tag", "root_local_name", "root_attributes", "namespaces", "namespace_provenance",
            "attribute_hints", "config_original_filename", "config_transform", "config_role");
        match.Metadata["config_role"].Should().Be("generic");
    }

    [Fact]
    public void XmlPluginClassifiesGenericWebOrAppConfig()
    {
        var content = Block("""
            <?xml version="1.0"?>
            <configuration>
              <appSettings />
            </configuration>
            """);
        var match = Detect("generic.config", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("structured-config-xml");
        match.Variant.Should().Be("web-or-app-config");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["config_role"].Should().Be("generic");

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Matched known configuration section tags used by web frameworks",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.0");
        match.Metadata.Keys.Should().Equal("xml_declaration", "root_tag", "root_local_name", "config_original_filename", "config_role");
    }

    [Fact]
    public void XmlPluginIdentifiesCustomConfigXml()
    {
        var content = Block("""
            <settings>
              <item key="a" value="1" />
            </settings>
            """);
        var match = Detect("custom.config", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("structured-config-xml");
        match.Variant.Should().Be("custom-config-xml");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["root_tag"].Should().Be("settings");

        match.Confidence.Should().BeApproximately(0.78, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .config suggests XML content",
            "Found XML element structure",
            "Root element <settings> is not the standard <configuration>",
            "Treating as vendor-specific .config XML",
            "Detected root element <settings>");
        match.Metadata.Keys.Should().Equal("root_tag", "root_local_name", "xml_well_formed");
        match.Metadata["xml_well_formed"].Should().Be(true);
    }

    [Fact]
    public void XmlPluginDetectsManifestVariant()
    {
        var content = Block("""
            <?xml version="1.0" encoding="utf-8"?>
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1">
              <assemblyIdentity name="App" version="1.0.0.0" />
            </assembly>
            """);
        var match = Detect("App.manifest", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("app-manifest-xml");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["root_local_name"].Should().Be("assembly");
        var provenance = Entries(match.Metadata["namespace_provenance"]);
        provenance.Should().NotBeEmpty();
        var defaultEntry = provenance[0];
        defaultEntry["attribute"].Should().Be("xmlns");
        defaultEntry["uri"].Should().Be("urn:schemas-microsoft-com:asm.v1");
        defaultEntry["line"].Should().BeOfType<int>().Which.Should().BeGreaterThanOrEqualTo(2);
        var namespaceReasons = match.Reasons.Where(reason => reason.Contains("namespace", StringComparison.OrdinalIgnoreCase)).ToList();
        namespaceReasons.Should().Contain(reason => reason.Contains("@L", StringComparison.Ordinal));

        // The declaration sits after leading whitespace, so the well-formedness probe fails exactly as expat does.
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .manifest suggests XML content",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "XML declared encoding utf-8",
            "Found XML element structure",
            "Matched assembly manifest namespace",
            "XML appears not well-formed within sampled content",
            "Detected root element <assembly>",
            "Recorded XML namespace declarations (default\u2192urn:schemas-microsoft-com:asm.v1 @L3)");
        match.Metadata.Keys.Should().Equal(
            "xml_declaration", "encoding", "root_tag", "root_local_name", "root_attributes", "namespaces", "namespace_provenance",
            "root_namespace", "xml_well_formed", "needs_review", "review_reasons");
        AssertMapping(match.Metadata["xml_declaration"], ("version", "1.0"), ("encoding", "utf-8"));
        match.Metadata["encoding"].Should().Be("utf-8");
        AssertMapping(
            defaultEntry,
            ("attribute", "xmlns"),
            ("prefix", null),
            ("uri", "urn:schemas-microsoft-com:asm.v1"),
            ("line", 3),
            ("column", 15),
            ("source", "root-attribute"),
            ("hash", "ed869e795745"));
        match.Metadata["root_namespace"].Should().Be("urn:schemas-microsoft-com:asm.v1");
        match.Metadata["xml_well_formed"].Should().Be(false);
        match.Metadata["needs_review"].Should().Be(true);
        Strings(match.Metadata["review_reasons"]).Should().Equal("XML not well-formed");
    }

    [Fact]
    public void XmlPluginDetectsManifestByExtensionWithoutNamespace()
    {
        var content = Block("""
            <assembly>
              <assemblyIdentity name="Bare" version="1.0.0.0" />
            </assembly>
            """);
        var match = Detect("Bare.manifest", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("app-manifest-xml");
        match.Reasons.Should().Contain(reason => reason.Contains("File extension .manifest", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.88, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .manifest suggests XML content",
            "Found XML element structure",
            "Detected root element <assembly>");
        match.Metadata!.Keys.Should().Equal("root_tag", "root_local_name", "xml_well_formed");
    }

    [Fact]
    public void XmlPluginDetectsResxVariantViaNamespace()
    {
        var content = Block("""
            <?xml version="1.0" encoding="utf-8"?>
            <root xmlns="http://schemas.microsoft.com/VisualStudio/2005/ResXSchema">
              <data name="Sample">
                <value>Hello</value>
              </data>
            </root>
            """);
        var match = Detect("Strings.resx", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("resource-xml");
        match.Metadata.Should().NotBeNull();
        Strings(match.Metadata!["resource_keys"]).Should().Equal("Sample");
        var provenance = Entries(match.Metadata["namespace_provenance"]);
        provenance.Should().NotBeEmpty();
        provenance[0]["attribute"].Should().Be("xmlns");
        match.Reasons.Should().Contain(reason => reason.Contains("Captured resource keys", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .resx suggests XML content",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "XML declared encoding utf-8",
            "Found XML element structure",
            "Detected .resx schema reference",
            "XML appears not well-formed within sampled content",
            "Detected root element <root>",
            "Recorded XML namespace declarations (default\u2192http://schemas.microsoft.com/VisualStudio/2005/ResXSchema @L3)",
            "Captured resource keys from .resx payload (e.g., Sample)");
        match.Metadata.Keys.Should().Equal(
            "xml_declaration", "encoding", "root_tag", "root_local_name", "root_attributes", "namespaces", "namespace_provenance",
            "root_namespace", "resource_keys", "resource_keys_preview", "xml_well_formed", "needs_review", "review_reasons");
        match.Metadata["resource_keys_preview"].Should().Be("Sample");
        provenance[0]["hash"].Should().Be("169993406bf6");
        provenance[0]["column"].Should().Be(11);
    }

    [Fact]
    public void XmlPluginDetectsResxByExtensionWithoutNamespace()
    {
        var content = Block("""
            <root>
              <data name="Sample">
                <value>Hello</value>
              </data>
            </root>
            """);
        var match = Detect("Strings.resx", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("resource-xml");
        match.Reasons.Should().Contain(reason => reason.Contains("File extension .resx", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.88, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .resx suggests XML content",
            "Found XML element structure",
            "Detected root element <root>");
        match.Metadata!.Keys.Should().Equal("root_tag", "root_local_name", "xml_well_formed");
    }

    [Fact]
    public void XmlPluginDetectsXamlVariantViaNamespace()
    {
        var content = Block("""
            <?xml version="1.0"?>
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid />
            </UserControl>
            """);
        var match = Detect("View.xaml", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("interface-xml");

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .xaml suggests XML content",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "Found XML element structure",
            "Found XAML namespace declaration",
            "XML appears not well-formed within sampled content",
            "Detected root element <UserControl>",
            "Recorded XML namespace declarations (default\u2192http://schemas.microsoft.com/winfx/2006/xaml/presentation @L3; x\u2192http://schemas.microsoft.com/winfx/2006/xaml @L4)");
        match.Metadata!.Keys.Should().Equal(
            "xml_declaration", "root_tag", "root_local_name", "root_attributes", "namespaces", "namespace_provenance",
            "root_namespace", "xml_well_formed", "needs_review", "review_reasons");
        AssertMapping(
            match.Metadata["namespaces"],
            ("default", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"),
            ("x", "http://schemas.microsoft.com/winfx/2006/xaml"));
        var provenance = Entries(match.Metadata["namespace_provenance"]);
        provenance.Should().HaveCount(2);
        provenance[1]["column"].Should().Be(18);
        provenance[1]["hash"].Should().Be("c64c2a0c1c56");
    }

    [Fact]
    public void XmlPluginDetectsXamlByExtensionWithoutNamespace()
    {
        var content = Block("""
            <UserControl>
              <Grid />
            </UserControl>
            """);
        var match = Detect("View.xaml", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("interface-xml");
        match.Reasons.Should().Contain(reason => reason.Contains("File extension .xaml", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.88, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .xaml suggests XML content",
            "Found XML element structure",
            "Detected root element <UserControl>");
    }

    [Fact]
    public void XmlPluginDetectsXsltVariant()
    {
        var content = Block("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <root />
              </xsl:template>
            </xsl:stylesheet>
            """);
        var match = Detect("layout.xslt", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("xslt-xml");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["xslt_stylesheet"].Should().Be(true);

        match.Confidence.Should().BeApproximately(0.93, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .xslt suggests XML content",
            "Found XML element structure",
            "File extension .xslt is commonly used for XSLT stylesheets",
            "Detected XSLT namespace declaration",
            "Root element <stylesheet> indicates an XSLT stylesheet",
            "Detected root element <xsl:stylesheet>",
            "Recorded XML namespace declarations (xsl\u2192http://www.w3.org/1999/XSL/Transform @L2)");
        match.Metadata.Keys.Should().Equal(
            "root_tag", "root_prefix", "root_local_name", "root_attributes", "namespaces", "namespace_provenance",
            "root_namespace", "xslt_stylesheet", "xml_well_formed");
        match.Metadata["root_tag"].Should().Be("xsl:stylesheet");
        match.Metadata["root_prefix"].Should().Be("xsl");
        match.Metadata["root_local_name"].Should().Be("stylesheet");
        match.Metadata["root_namespace"].Should().Be("http://www.w3.org/1999/XSL/Transform");
        match.Metadata["xml_well_formed"].Should().Be(true);
    }

    [Theory]
    [InlineData("nlog.config", "<nlog><targets /></nlog>", "nlog-config", "Root element indicates NLog logging configuration", "nlog")]
    [InlineData("log4net.config", "<log4net><appender /></log4net>", "log4net-config", "Root element indicates log4net logging configuration", "log4net")]
    [InlineData("serilog.config", "<serilog><writeTo /></serilog>", "serilog-config", "Root element indicates Serilog logging configuration", "serilog")]
    public void XmlPluginDetectsVendorConfigRoots(string filename, string payload, string expected, string vendorReason, string root)
    {
        var match = Detect(filename, payload);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("structured-config-xml");
        match.Variant.Should().Be(expected);

        match.Confidence.Should().BeApproximately(0.9, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .config suggests XML content",
            "Found XML element structure",
            vendorReason,
            $"Detected root element <{root}>");
        match.Metadata!.Keys.Should().Equal("root_tag", "root_local_name", "xml_well_formed");
    }

    [Fact]
    public void XmlPluginCanonicalisesRootAttributes()
    {
        var content = Block("""
            <configuration attrB="  value-b " attrA="value-a" xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
              <appSettings />
            </configuration>
            """);
        var match = Detect("web.config", content);

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        AssertMapping(
            match.Metadata!["root_attributes"],
            ("attrA", "value-a"),
            ("attrB", "value-b"),
            ("xmlns:xdt", "http://schemas.microsoft.com/XML-Document-Transform"));

        match.Variant.Should().Be("web-config-transform");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Matched known configuration section tags used by web frameworks",
            "Detected root element <configuration>",
            "Recorded XML namespace declarations (xdt\u2192http://schemas.microsoft.com/XML-Document-Transform @L2)",
            "Filename web.config strongly suggests web-hosted configuration",
            "Detected XML-Document-Transform namespace declaration (xdt)");
        match.Metadata.Keys.Should().Equal(
            "root_tag", "root_local_name", "root_attributes", "namespaces", "namespace_provenance", "config_original_filename",
            "config_transform", "config_transform_scope", "config_role");
        Entries(match.Metadata["namespace_provenance"])[0]["column"].Should().Be(55);
    }

    [Fact]
    public void XmlPluginExtractsSchemaLocations()
    {
        var content = Block("""
            <?xml version="1.0"?>
            <configuration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                           xsi:schemaLocation="http://schemas.microsoft.com/.NetConfiguration/v2.0 http://schemas.microsoft.com/.NetConfiguration/v2.0/Configuration.xsd">
              <appSettings />
            </configuration>
            """);
        var match = Detect("web.config", content);

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        var schemaLocations = Entries(match.Metadata!["schema_locations"]);
        schemaLocations.Should().HaveCount(1);
        AssertMapping(
            schemaLocations[0],
            ("namespace", "http://schemas.microsoft.com/.NetConfiguration/v2.0"),
            ("location", "http://schemas.microsoft.com/.NetConfiguration/v2.0/Configuration.xsd"));
        const string schemaReason = "Schema http://schemas.microsoft.com/.NetConfiguration/v2.0/Configuration.xsd declared";
        match.Reasons.Should().Contain(reason => reason.Contains(schemaReason, StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Matched known configuration section tags used by web frameworks",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "Recorded XML namespace declarations (xsi\u2192http://www.w3.org/2001/XMLSchema-instance @L3)",
            "Schema http://schemas.microsoft.com/.NetConfiguration/v2.0/Configuration.xsd declared for namespace http://schemas.microsoft.com/.NetConfiguration/v2.0",
            "Filename web.config strongly suggests web-hosted configuration");
        match.Metadata.Keys.Should().Equal(
            "xml_declaration", "root_tag", "root_local_name", "root_attributes", "namespaces", "namespace_provenance",
            "schema_locations", "config_original_filename", "config_role");
        AssertMapping(
            match.Metadata["root_attributes"],
            ("xmlns:xsi", "http://www.w3.org/2001/XMLSchema-instance"),
            ("xsi:schemaLocation", "http://schemas.microsoft.com/.NetConfiguration/v2.0 http://schemas.microsoft.com/.NetConfiguration/v2.0/Configuration.xsd"));
    }

    [Fact]
    public void XmlPluginCollectsAttributeHints()
    {
        var content = Block("""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <connectionStrings>
                <add name="DefaultConnection" connectionString="Server=.;Database=App;User Id=app;Password=Pass123!;" />
              </connectionStrings>
              <appSettings>
                <add key="ServiceEndpoint" value="https://api.example.com/v1/" />
                <add key="FeatureFlag:NewUI" value="true" />
              </appSettings>
              <system.serviceModel>
                <client>
                  <endpoint address="net.tcp://services.example.com:8443/Feed" />
                </client>
              </system.serviceModel>
            </configuration>
            """);
        var match = Detect("web.config", content);

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        var hints = Mapping(match.Metadata!["attribute_hints"]);

        var connectionHints = Entries(hints["connection_strings"]);
        connectionHints.Should().HaveCount(1);
        var connectionEntry = connectionHints[0];
        connectionEntry["hash"].Should().Be(Sha256("Server=.;Database=App;User Id=app;Password=Pass123!;"));
        connectionEntry["key"].Should().Be("DefaultConnection");

        var endpointHints = Entries(hints["service_endpoints"]);
        var endpointHashes = endpointHints.Select(entry => entry["hash"]).ToList();
        endpointHashes.Should().Contain(Sha256("https://api.example.com/v1/"));
        endpointHashes.Should().Contain(Sha256("net.tcp://services.example.com:8443/Feed"));

        var featureHints = Entries(hints["feature_flags"]);
        featureHints.Should().NotBeEmpty();
        featureHints[0]["key"].Should().Be("FeatureFlag:NewUI");
        match.Reasons.Should().Contain(reason => reason.Contains("feature flag attribute hints", StringComparison.OrdinalIgnoreCase));

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Matched known configuration section tags used by web frameworks",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "XML declared encoding utf-8",
            "Captured connection string attribute hints",
            "Captured service endpoint attribute hints",
            "Captured feature flag attribute hints",
            "Filename web.config strongly suggests web-hosted configuration");
        match.Metadata.Keys.Should().Equal(
            "xml_declaration", "encoding", "root_tag", "root_local_name", "attribute_hints", "config_original_filename", "config_role");
        AssertAttributeHintEntries(hints);
    }

    private static void AssertAttributeHintEntries(OrderedDictionary<string, object?> hints)
    {
        hints.Keys.Should().Equal("connection_strings", "service_endpoints", "feature_flags");
        var endpointHints = Entries(hints["service_endpoints"]);
        var featureHints = Entries(hints["feature_flags"]);
        AssertMapping(
            Entries(hints["connection_strings"])[0],
            ("element", "add"),
            ("attribute", "connectionString"),
            ("hash", "13f3f3bddd00563f609aab97004554b2c8b2797ca49abd9e72d61b7546b00a6e"),
            ("length", 52),
            ("key", "DefaultConnection"),
            ("key_attribute", "name"));
        endpointHints.Should().HaveCount(2);
        AssertMapping(
            endpointHints[0],
            ("element", "add"),
            ("attribute", "value"),
            ("hash", "51bd629cdbf94efed0ef5b5bf2d9fb593236b0186ffa1bc1482c75cc277bf9ff"),
            ("length", 27),
            ("key", "ServiceEndpoint"),
            ("key_attribute", "key"));
        AssertMapping(
            endpointHints[1],
            ("element", "endpoint"),
            ("attribute", "address"),
            ("hash", "ed0df07007e91270a9ef52147abc17357398d2db9e86ac040228c43bd4c9ba52"),
            ("length", 40));
        AssertMapping(
            featureHints[0],
            ("element", "add"),
            ("attribute", "value"),
            ("hash", "b5bea41b6c623f7c09f1bf24dcae58ebab3c0cdd90ad966bc43a45b44867e12b"),
            ("length", 4),
            ("key", "FeatureFlag:NewUI"),
            ("key_attribute", "key"));
    }

    [Fact]
    public void XmlPluginCollectsFeatureToggleAttributeHints()
    {
        var content = Block("""
            <configuration>
              <FeatureToggle name="NewUI" value="enabled" />
            </configuration>
            """);
        var match = Detect("feature.config", content);

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        var hints = Mapping(match.Metadata!["attribute_hints"]);
        var featureHints = Entries(hints["feature_flags"]);
        featureHints.Should().NotBeEmpty();
        featureHints[0]["key"].Should().Be("NewUI");

        match.Confidence.Should().BeApproximately(0.94, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Detected root element <configuration>",
            "Captured feature flag attribute hints");
        hints.Keys.Should().Equal("feature_flags");
        AssertMapping(
            featureHints[0],
            ("element", "FeatureToggle"),
            ("attribute", "value"),
            ("hash", "fb9cf75606b4070dd6a9705810906bba28d0e2ea74ff301b999a91dbb68c7d98"),
            ("length", 7),
            ("key", "NewUI"),
            ("key_attribute", "name"));
    }

    [Fact]
    public void XmlPluginSupportsTargetsExtension()
    {
        var content = Block("""
            <Project ToolsVersion="Current"
                     DefaultTargets="Build;Publish"
                     xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Import Project="$(VSToolsPath)\WebApplication.targets" Condition="Exists('$(VSToolsPath)')" />
              <Target Name="Publish">
                <Message Text="Publishing" />
              </Target>
            </Project>
            """);
        var match = Detect("build.targets", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("msbuild-targets");
        match.Metadata.Should().NotBeNull();
        Strings(match.Metadata!["msbuild_default_targets"]).Should().Equal("Build", "Publish");
        match.Metadata["msbuild_tools_version"].Should().Be("Current");
        Strings(match.Metadata["msbuild_targets"]).Should().Equal("Publish");
        var importHints = Entries(match.Metadata["msbuild_import_hints"]);
        importHints.Should().NotBeEmpty();
        importHints[0]["attribute"].Should().Be("Project");
        match.Reasons.Should().Contain(reason => reason.Contains("MSBuild default targets", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .targets suggests XML content",
            "Found XML element structure",
            "Root element <Project> indicates an MSBuild targets layout",
            "Detected root element <Project>",
            "Recorded XML namespace declarations (default\u2192http://schemas.microsoft.com/developer/msbuild/2003 @L4)",
            "MSBuild default targets declared (Build, Publish)",
            "MSBuild ToolsVersion set to Current",
            "Captured MSBuild target declarations (Publish)",
            "Captured MSBuild import references");
        match.Metadata.Keys.Should().Equal(
            "root_tag", "root_local_name", "root_attributes", "namespaces", "namespace_provenance", "root_namespace",
            "msbuild_detected", "msbuild_kind", "msbuild_default_targets", "msbuild_tools_version", "msbuild_targets",
            "msbuild_import_hints", "xml_well_formed");
        AssertMapping(
            match.Metadata["root_attributes"],
            ("DefaultTargets", "Build;Publish"),
            ("ToolsVersion", "Current"),
            ("xmlns", "http://schemas.microsoft.com/developer/msbuild/2003"));
        AssertMapping(
            importHints[0],
            ("attribute", "Project"),
            ("hash", "2589d7f9fdfb092b27ec2724b2dafe3d0a1f3e436f953eb7a20acf7ea732c481"),
            ("length", 37),
            ("condition_hash", "c88773d6c79c34497258f7f7432f9b5d05c326c9f2ded6c60b6c51652da05785"),
            ("condition_length", 24));
        match.Metadata["xml_well_formed"].Should().Be(true);
    }

    [Fact]
    public void XmlPluginDetectsMsbuildPropsVariant()
    {
        var content = Block("""
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Import Project="shared.targets" />
            </Project>
            """);
        var match = Detect("common.props", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("msbuild-props");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["msbuild_kind"].Should().Be("props");
        match.Metadata["msbuild_detected"].Should().Be(true);

        match.Confidence.Should().BeApproximately(0.94, 1e-9);
        match.Reasons.Should().Equal(
            "Found XML element structure",
            "Root element <Project> indicates an MSBuild props layout",
            "Detected root element <Project>",
            "Recorded XML namespace declarations (default\u2192http://schemas.microsoft.com/developer/msbuild/2003 @L2)",
            "Captured MSBuild import references");
        match.Metadata.Keys.Should().Equal(
            "root_tag", "root_local_name", "root_attributes", "namespaces", "namespace_provenance", "root_namespace",
            "msbuild_detected", "msbuild_kind", "msbuild_import_hints", "xml_well_formed");
        AssertMapping(
            Entries(match.Metadata["msbuild_import_hints"])[0],
            ("attribute", "Project"),
            ("hash", "f65929a811eed7b5d4e23a5c7394461e1b791721f4c42bc57d506cb40140b8a3"),
            ("length", 14));
    }

    [Fact]
    public void XmlPluginDetectsMsbuildWithDoctypeAndAttributes()
    {
        var content = Block("""
            <?xml version="1.0"?>
            <!DOCTYPE Project>
            <Project DefaultTargets="Build" ToolsVersion="Current">
              <Target Name="Pack" />
            </Project>
            """);
        var match = new XmlPlugin().Detect("Example.csproj", Encoding.UTF8.GetBytes(content), content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("msbuild-project");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["msbuild_detected"].Should().Be(true);
        match.Metadata["doctype"].Should().Be("Project");
        match.Reasons.Should().Contain(reason => reason.Contains("DOCTYPE", StringComparison.Ordinal));

        // The DOCTYPE guard blocks the tree parse, so no targets are captured; the probe fails on the indented declaration.
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Detected XML declaration",
            "XML version declared as 1.0",
            "Found XML element structure",
            "Root element <Project> indicates an MSBuild project definition",
            "XML appears not well-formed within sampled content",
            "Detected root element <Project>",
            "Document declares DOCTYPE Project");
        match.Metadata.Keys.Should().Equal(
            "xml_declaration", "doctype", "root_tag", "root_local_name", "root_attributes", "msbuild_detected", "msbuild_kind",
            "xml_well_formed", "needs_review", "review_reasons");
        match.Metadata["msbuild_kind"].Should().Be("project");
    }

    [Fact]
    public void XmlPluginDetectsMsbuildProjectMetadata()
    {
        var content = Block("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <Import Sdk="Microsoft.Build.NoTargets/1.0.0" />
              <Target Name="Pack" />
            </Project>
            """);
        var match = Detect("App.csproj", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("msbuild-project");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["msbuild_sdk"].Should().Be("Microsoft.NET.Sdk");
        Strings(match.Metadata["msbuild_targets"]).Should().Equal("Pack");
        var importHints = Entries(match.Metadata["msbuild_import_hints"]);
        importHints.Should().NotBeEmpty();
        importHints[0]["attribute"].Should().Be("Sdk");
        match.Reasons.Should().Contain(reason => reason.Contains("MSBuild SDK specified", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.945, 1e-9);
        match.Reasons.Should().Equal(
            "Found XML element structure",
            "Root element <Project> indicates an MSBuild project definition",
            "Detected root element <Project>",
            "MSBuild SDK specified (Microsoft.NET.Sdk)",
            "Captured MSBuild target declarations (Pack)",
            "Captured MSBuild import references");
        match.Metadata.Keys.Should().Equal(
            "root_tag", "root_local_name", "root_attributes", "msbuild_detected", "msbuild_kind", "msbuild_sdk", "msbuild_targets",
            "msbuild_import_hints", "xml_well_formed");
        AssertMapping(
            importHints[0],
            ("attribute", "Sdk"),
            ("hash", "e62cac5f57fe541dd282f649c07b335d4a852ea9fd8442829807cd03576ed64c"),
            ("length", 31));
    }

    [Fact]
    public void XmlPluginDetectsGenericXmlVariant()
    {
        var content = Block("""
            <notes>
              <note>Hello</note>
            </notes>
            """);
        var match = Detect("notes.xml", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("generic");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["root_tag"].Should().Be("notes");

        match.Confidence.Should().BeApproximately(0.73, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .xml suggests XML content",
            "Found XML element structure",
            "Detected root element <notes>");
        match.Metadata.Keys.Should().Equal("root_tag", "root_local_name", "xml_well_formed");
    }

    [Fact]
    public void XmlPluginDetectsExtensionlessXmlPayload()
    {
        var content = Block("""
            <root>
              <item>Hello</item>
            </root>
            """);
        var match = new XmlPlugin().Detect("CONFIG", Encoding.UTF8.GetBytes(content), content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("generic");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["root_tag"].Should().Be("root");

        match.Confidence.Should().BeApproximately(0.73, 1e-9);
        match.Reasons.Should().Equal("Found XML element structure", "Detected root element <root>");
        match.Metadata.Keys.Should().Equal("root_tag", "root_local_name", "xml_well_formed");
    }

    [Fact]
    public void XmlPluginRejectsPlainText()
    {
        Detect("plain.txt", "Just text without any XML markers").Should().BeNull();
    }

    [Fact]
    public void XmlPluginReturnsNoneWithoutText()
    {
        new XmlPlugin().Detect("config.xml", [], null).Should().BeNull();
    }

    [Fact]
    public void XmlPluginHandlesNamespacedConfigurationRoot()
    {
        var content = Block("""
            <ns:configuration xmlns:ns="urn:custom">
              <ns:appSettings />
            </ns:configuration>
            """);
        var match = Detect("web.config", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("structured-config-xml");

        match.Variant.Should().Be("web-config");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Detected root element <ns:configuration>",
            "Recorded XML namespace declarations (ns\u2192urn:custom @L2)",
            "Filename web.config strongly suggests web-hosted configuration");
        match.Metadata!.Keys.Should().Equal(
            "root_tag", "root_prefix", "root_local_name", "root_attributes", "namespaces", "namespace_provenance", "root_namespace",
            "config_original_filename", "config_role");
        match.Metadata["root_prefix"].Should().Be("ns");
        match.Metadata["root_namespace"].Should().Be("urn:custom");
    }

    [Fact]
    public void XmlPluginManifestWithPrefixedRoot()
    {
        var match = Detect("Prefixed.manifest", Block(PrefixedManifestContent));

        match.Should().NotBeNull();
        match!.Variant.Should().Be("app-manifest-xml");

        match.Confidence.Should().BeApproximately(0.91, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .manifest suggests XML content",
            "Found XML element structure",
            "Matched assembly manifest namespace",
            "Detected root element <asm:assembly>",
            "Recorded XML namespace declarations (default\u2192urn:schemas-microsoft-com:asm.v1 @L2; asm\u2192urn:custom @L2)");
        match.Metadata!.Keys.Should().Equal(
            "root_tag", "root_prefix", "root_local_name", "root_attributes", "namespaces", "namespace_provenance", "root_namespace",
            "xml_well_formed");
        // The namespace map is sorted by prefix; the root namespace follows the root prefix, not the default declaration.
        AssertMapping(match.Metadata["namespaces"], ("asm", "urn:custom"), ("default", "urn:schemas-microsoft-com:asm.v1"));
        match.Metadata["root_namespace"].Should().Be("urn:custom");
        var provenance = Entries(match.Metadata["namespace_provenance"]);
        provenance.Select(entry => entry["column"]).Should().Equal(19, 60);
        provenance.Select(entry => entry["hash"]).Should().Equal("ed869e795745", "a06c00825067");
    }

    [Fact]
    public void XmlPluginConfigurationWithoutConfigExtension()
    {
        var content = Block("""
            <configuration>
              <system.web />
            </configuration>
            """);
        var match = Detect("settings.xml", content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("structured-config-xml");

        match.Variant.Should().Be("web-config");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .xml suggests XML content",
            "Found XML element structure",
            "Root element indicates framework configuration layout",
            "Detected web-specific sections such as <system.web> or <system.webServer>",
            "Detected root element <configuration>");
        match.Metadata!.Keys.Should().Equal("root_tag", "root_local_name", "config_role", "xml_well_formed");
        match.Metadata.Should().NotContainKey("config_original_filename");
    }

    [Fact]
    public void XmlPluginIdentifiesExeConfigRole()
    {
        var match = Detect("client.exe.config", Block(StartupContent));

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["config_role"].Should().Be("app");
        match.Reasons.Should().Contain(reason => reason.Contains(".exe.config", StringComparison.Ordinal));

        match.Variant.Should().Be("app-config");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Detected root element <configuration>",
            "Filename ending with .exe.config or .dll.config typically ships beside framework binaries");
        match.Metadata.Keys.Should().Equal("root_tag", "root_local_name", "config_original_filename", "config_role");
    }

    [Fact]
    public void XmlPluginSchemaLocationReasons()
    {
        var content = Block("""
            <configuration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                           xsi:schemaLocation="urn:custom schema.xsd">
              <appSettings />
            </configuration>
            """);
        var match = Detect("schema.config", content);

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        var schemaLocations = Entries(match.Metadata!["schema_locations"]);
        schemaLocations.Should().NotBeEmpty();
        schemaLocations[0]["location"].Should().Be("schema.xsd");
        match.Reasons.Should().Contain(reason => reason.Contains("Schema schema.xsd", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Matched known configuration section tags used by web frameworks",
            "Detected root element <configuration>",
            "Recorded XML namespace declarations (xsi\u2192http://www.w3.org/2001/XMLSchema-instance @L2)",
            "Schema schema.xsd declared for namespace urn:custom");
        AssertMapping(schemaLocations[0], ("namespace", "urn:custom"), ("location", "schema.xsd"));
        match.Metadata["config_role"].Should().Be("generic");
    }

    [Fact]
    public void XmlPluginMsbuildMetadataDedupesImports()
    {
        var content = Block("""
            <Project DefaultTargets="Build;Pack"
                     ToolsVersion="15.0"
                     xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Import Project="shared.props" />
              <Import Project="shared.props" />
              <Target Name="Build" />
              <Target Name="Pack" />
            </Project>
            """);
        var match = Detect("duplicate.targets", content);

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        var hints = Entries(match.Metadata!["msbuild_import_hints"]);
        hints.Should().HaveCount(1);
        Strings(match.Metadata["msbuild_targets"]).Should().Equal("Build", "Pack");

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .targets suggests XML content",
            "Found XML element structure",
            "Root element <Project> indicates an MSBuild targets layout",
            "Detected root element <Project>",
            "Recorded XML namespace declarations (default\u2192http://schemas.microsoft.com/developer/msbuild/2003 @L4)",
            "MSBuild default targets declared (Build, Pack)",
            "MSBuild ToolsVersion set to 15.0",
            "Captured MSBuild target declarations (Build, Pack)",
            "Captured MSBuild import references");
        AssertMapping(
            hints[0],
            ("attribute", "Project"),
            ("hash", "918553e4bb44f8db78092a4dd2c97daaa44fddffe219ced30b7bed10c5894669"),
            ("length", 12));
        Strings(match.Metadata["msbuild_default_targets"]).Should().Equal("Build", "Pack");
        match.Metadata["msbuild_tools_version"].Should().Be("15.0");
    }

    [Fact]
    public void XmlPluginAttributeHintDedupesEntries()
    {
        var content = Block("""
            <configuration>
              <connectionStrings>
                <add name="Default" connectionString="Server=tcp://sql" />
                <add name="Default" connectionString="Server=tcp://sql" />
              </connectionStrings>
              <serviceEndpoints>
                <endpoint name="FileShare" address="\\server\share" />
              </serviceEndpoints>
              <features>
                <feature name="NewUI" value="true" />
                <feature name="NewUI" value="true" />
              </features>
            </configuration>
            """);
        var match = Detect("hints.config", content);

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        var hints = Mapping(match.Metadata!["attribute_hints"]);
        Entries(hints["connection_strings"]).Should().HaveCount(1);
        Entries(hints["feature_flags"]).Should().HaveCount(1);
        XmlPlugin.LooksLikeEndpoint(@"\\server\share").Should().BeTrue();
        XmlPlugin.LooksLikeEndpoint("net.tcp://service").Should().BeTrue();

        match.Confidence.Should().BeApproximately(0.94, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Detected root element <configuration>",
            "Captured connection string attribute hints",
            "Captured service endpoint attribute hints",
            "Captured feature flag attribute hints");
        AssertMapping(
            Entries(hints["service_endpoints"])[0],
            ("element", "endpoint"),
            ("attribute", "address"),
            ("hash", "05e24481d49af5ff4b88980216126fdaa1506fb0e7dcdbc60d64e5bcc5a61312"),
            ("length", 14),
            ("key", "FileShare"),
            ("key_attribute", "name"));
        AssertMapping(
            Entries(hints["feature_flags"])[0],
            ("element", "feature"),
            ("attribute", "value"),
            ("hash", "b5bea41b6c623f7c09f1bf24dcae58ebab3c0cdd90ad966bc43a45b44867e12b"),
            ("length", 4),
            ("key", "NewUI"),
            ("key_attribute", "name"));
    }

    [Fact]
    public void XmlPluginParseLimitBlocksLargeInput()
    {
        var plugin = new XmlPlugin { MaxSafeParseChars = 10 };
        var metadata = plugin.CollectMetadata("<root>" + new string('a', 50) + "</root>", ".xml");
        metadata["root_tag"].Should().Be("root");
        metadata.Keys.Should().Equal("root_tag", "root_local_name");
    }

    [Fact]
    public void XmlPluginFallbackParserWithoutDefused()
    {
        var plugin = new XmlPlugin { DefusedAvailable = false };
        var calls = new Dictionary<string, object?>(StringComparer.Ordinal);
        var original = plugin.FallbackFromString;
        plugin.FallbackFromString = (text, parser) =>
        {
            calls["parser"] = parser;
            return original(text, parser);
        };

        plugin.CollectMetadata("<root />", ".xml");
        calls.GetValueOrDefault("parser").Should().NotBeNull();
    }

    // Python adds each MSBuild increment to the running bonus; a separate subtotal gives 0.905 instead of this double.
    [Fact]
    public void MsbuildBonusAccumulatesInPythonOrder()
    {
        const string content = "<\u212eP Sdk=\"Microsoft.NET.Sdk\"><\u212e:Target xmlns:\u212e=\"u\" Name=\"Build\"/>"
            + "<\u212e:Import xmlns:\u212e=\"u\" Project=\"a.props\"/></\u212eP>";
        var match = new XmlPlugin().Detect("confidence-float-order-msbuild.targets", Encoding.UTF8.GetBytes(content), content);

        match.Should().NotBeNull();
        match!.Confidence.Should().Be(0.9049999999999999);
        match.Metadata!.Keys.Should().Equal(
            "msbuild_detected", "msbuild_kind", "msbuild_sdk", "msbuild_targets", "msbuild_import_hints", "xml_well_formed");
    }

    // Without defusedxml the tree keeps comments inside the document element; their tag is the Comment function, so
    // Python's resx and MSBuild walks raise TypeError on reaching one, while the attribute-hint walk skips it (no attrib).
    [Theory]
    [InlineData("<Project><!-- c --><Target Name=\"A\"/></Project>", ".csproj")]
    [InlineData("<root xmlns=\"http://schemas.microsoft.com/resx\"><!-- c --><data name=\"k\"/></root>", ".resx")]
    public void FallbackTreeCommentsRaiseInResxAndMsbuildWalks(string text, string extension)
    {
        var plugin = new XmlPlugin { DefusedAvailable = false };

        var act = () => plugin.CollectMetadata(text, extension);

        act.Should().Throw<InvalidOperationException>().WithMessage("argument of type 'function' is not iterable");
        new XmlPlugin().CollectMetadata(text, extension).Should().NotBeEmpty();
    }

    [Fact]
    public void FallbackTreeCommentsOutsideTheWalksLeaveMetadataUnchanged()
    {
        var plugin = new XmlPlugin { DefusedAvailable = false };

        var topLevel = plugin.CollectMetadata("<!-- a --><Project Sdk=\"x\"/><!-- b -->", ".csproj");
        topLevel.Keys.Should().Equal("root_tag", "root_local_name", "root_attributes", "msbuild_detected", "msbuild_kind", "msbuild_sdk");
        topLevel["msbuild_sdk"].Should().Be("x");

        var hints = plugin.CollectMetadata("<root><!-- c --><add key=\"FeatureX\" value=\"true\"/></root>", ".config");
        hints.Keys.Should().Equal("root_tag", "root_local_name", "attribute_hints");
        var flag = ((OrderedDictionary<string, object?>)hints["attribute_hints"]!)["feature_flags"]
            .Should().BeAssignableTo<List<OrderedDictionary<string, object?>>>().Subject.Should().ContainSingle().Subject;
        flag.Keys.Should().Equal("element", "attribute", "hash", "length", "key", "key_attribute");
        flag["hash"].Should().Be("b5bea41b6c623f7c09f1bf24dcae58ebab3c0cdd90ad966bc43a45b44867e12b");
    }

    [Fact]
    public void XmlPluginDetectsConfigWhenRegexMissesRoot()
    {
        var content = Stripped("""
            <?xml version='1.0'?>
            <configuration/>
            """);
        var match = new XmlPlugin().Detect("custom.config", Encoding.UTF8.GetBytes(content), content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("structured-config-xml");
        match.Variant.Should().Be("web-or-app-config");
        match.Reasons.Should().Contain(reason => reason.Contains("framework configuration layout", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Root element indicates framework configuration layout",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.0");
        match.Metadata!.Keys.Should().Equal("xml_declaration", "root_tag", "root_local_name", "config_original_filename", "config_role");
    }

    [Fact]
    public void XmlPluginManifestNamespaceWithoutExtension()
    {
        var content = Stripped(PrefixedManifestContent);
        var match = new XmlPlugin().Detect("assembly.xml", Encoding.UTF8.GetBytes(content), content);

        match.Should().NotBeNull();
        match!.Variant.Should().Be("app-manifest-xml");
        match.Reasons.Should().Contain(reason => reason.Contains("assembly manifest namespace", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.91, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .xml suggests XML content",
            "Found XML element structure",
            "Matched assembly manifest namespace",
            "Detected root element <asm:assembly>",
            "Recorded XML namespace declarations (default\u2192urn:schemas-microsoft-com:asm.v1 @L1; asm\u2192urn:custom @L1)");
        match.Metadata!["xml_well_formed"].Should().Be(true);
    }

    [Fact]
    public void XmlPluginDetectsManifestByContentScan()
    {
        var content = Stripped("""
            <root>
              urn:schemas-microsoft-com:asm.v1
            </root>
            """);
        var match = new XmlPlugin().Detect("manifest.txt", Encoding.UTF8.GetBytes(content), content);

        match.Should().NotBeNull();
        match!.Variant.Should().Be("app-manifest-xml");
        match.Reasons.Should().Contain(reason => reason.Contains("assembly manifest namespace", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.88, 1e-9);
        match.Reasons.Should().Equal("Found XML element structure", "Matched assembly manifest namespace", "Detected root element <root>");
        match.Metadata!.Keys.Should().Equal("root_tag", "root_local_name", "xml_well_formed");
    }

    [Fact]
    public void XmlPluginDetectsResxByContentScan()
    {
        var content = Stripped("""
            <root>
              http://schemas.microsoft.com/VisualStudio/2005/ResXSchema
            </root>
            """);
        var match = new XmlPlugin().Detect("resources.txt", Encoding.UTF8.GetBytes(content), content);

        match.Should().NotBeNull();
        match!.Variant.Should().Be("resource-xml");
        match.Reasons.Should().Contain(reason => reason.Contains("resx schema", StringComparison.OrdinalIgnoreCase));

        match.Confidence.Should().BeApproximately(0.88, 1e-9);
        match.Reasons.Should().Equal("Found XML element structure", "Detected .resx schema reference", "Detected root element <root>");
    }

    [Fact]
    public void XmlPluginDetectsXamlNamespaceByContent()
    {
        var content = Stripped("""
            <root>
              http://schemas.microsoft.com/winfx/2006/xaml/presentation
            </root>
            """);
        var match = new XmlPlugin().Detect("view.txt", Encoding.UTF8.GetBytes(content), content);

        match.Should().NotBeNull();
        match!.Variant.Should().Be("interface-xml");
        match.Reasons.Should().Contain(reason => reason.Contains("xaml", StringComparison.OrdinalIgnoreCase));

        match.Confidence.Should().BeApproximately(0.88, 1e-9);
        match.Reasons.Should().Equal("Found XML element structure", "Found XAML namespace declaration", "Detected root element <root>");
    }

    [Fact]
    public void XmlPluginRecordsXmlDeclarationDetails()
    {
        var content = Stripped("""
            <?xml version="1.1" encoding="UTF-16" standalone="no"?>
            <configuration>
              <appSettings />
            </configuration>
            """);
        var match = Detect("details.config", content);

        match.Should().NotBeNull();
        match!.Reasons.Should().Contain(reason => reason.Contains("encoding", StringComparison.OrdinalIgnoreCase));
        match.Reasons.Should().Contain(reason => reason.Contains("standalone", StringComparison.OrdinalIgnoreCase));

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Matched known configuration section tags used by web frameworks",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.1",
            "XML declared encoding UTF-16",
            "XML standalone flag is no");
        match.Metadata!.Keys.Should().Equal("xml_declaration", "encoding", "root_tag", "root_local_name", "config_original_filename", "config_role");
        AssertMapping(match.Metadata["xml_declaration"], ("version", "1.1"), ("encoding", "UTF-16"), ("standalone", "no"));
        match.Metadata["encoding"].Should().Be("UTF-16");
    }

    [Fact]
    public void XmlPluginSchemaReasonWithoutNamespace()
    {
        var content = Stripped("""
            <?xml version='1.0'?>
            <configuration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                           xsi:noNamespaceSchemaLocation="local.xsd">
              <appSettings />
            </configuration>
            """);
        var match = Detect("schema.config", content);

        match.Should().NotBeNull();
        match!.Reasons.Should().Contain(reason => reason.Contains("default namespace", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Matched known configuration section tags used by web frameworks",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "Recorded XML namespace declarations (xsi\u2192http://www.w3.org/2001/XMLSchema-instance @L2)",
            "Schema local.xsd declared for default namespace");
        AssertMapping(Entries(match.Metadata!["schema_locations"])[0], ("namespace", null), ("location", "local.xsd"));
    }

    [Fact]
    public void XmlPluginDetectsAppHintWithoutFilename()
    {
        var content = Stripped(StartupContent);
        var match = new XmlPlugin().Detect("settings.config", Encoding.UTF8.GetBytes(content), content);

        match.Should().NotBeNull();
        match!.Variant.Should().Be("app-config");
        match.Reasons.Should().Contain(reason => reason.Contains("application configuration sections", StringComparison.Ordinal));

        match.Confidence.Should().BeApproximately(0.94, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Detected root element <configuration>",
            "Detected application configuration sections like <startup> or <supportedRuntime>");
        match.Metadata!.Keys.Should().Equal("root_tag", "root_local_name", "config_original_filename", "config_role");
    }

    // Fixture checks beyond the Python suite: provenance hashes and confidence pinned from the Python oracle.

    private static DetectionMatch? DetectFixture(params string[] segments)
    {
        var path = RepoPaths.Fixtures(segments);
        var data = File.ReadAllBytes(path);
        return new XmlPlugin().Detect(path, data, FormatRegistry.DecodeText(data).Text);
    }

    [Fact]
    public void NamespaceProvenanceFixtureRecordsEveryDeclaration()
    {
        var match = DetectFixture("xml", "namespace_provenance_sample.xml");

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("xml");
        match.Variant.Should().Be("generic");
        match.Confidence.Should().BeApproximately(0.81, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .xml suggests XML content",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "XML declared encoding utf-8",
            "Found XML element structure",
            "Detected root element <assembly>",
            "Recorded XML namespace declarations (default\u2192urn:example:driftbuster:manifest @L2; compat\u2192urn:example:driftbuster:compatibility @L3; provenance\u2192urn:example:driftbuster:provenance @L4)");
        match.Metadata!.Keys.Should().Equal(
            "xml_declaration", "encoding", "root_tag", "root_local_name", "root_attributes", "namespaces", "namespace_provenance",
            "root_namespace", "xml_well_formed");
        AssertMapping(
            match.Metadata["namespaces"],
            ("compat", "urn:example:driftbuster:compatibility"),
            ("default", "urn:example:driftbuster:manifest"),
            ("provenance", "urn:example:driftbuster:provenance"));
        var provenance = Entries(match.Metadata["namespace_provenance"]);
        provenance.Select(entry => entry["attribute"]).Should().Equal("xmlns", "xmlns:compat", "xmlns:provenance");
        provenance.Select(entry => entry["line"]).Should().Equal(2, 3, 4);
        provenance.Select(entry => entry["column"]).Should().Equal(11, 11, 11);
        provenance.Select(entry => entry["hash"]).Should().Equal("161b031d0c0c", "8ac5816bc880", "7fc8c7ffaee8");
        match.Metadata["xml_well_formed"].Should().Be(true);
    }

    [Theory]
    [InlineData("web.config", "web-config", "web", "Filename web.config strongly suggests web-hosted configuration")]
    [InlineData("App.config", "app-config", "app", "Filename app.config indicates per-application configuration")]
    [InlineData("machine.config", "machine-config", "machine", "Filename machine.config indicates machine-wide configuration")]
    public void ConfigFixturesClassifyByFilename(string name, string variant, string role, string filenameReason)
    {
        var match = DetectFixture("config", name);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("structured-config-xml");
        match.Variant.Should().Be(variant);
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Metadata!["config_role"].Should().Be(role);
        match.Metadata["config_original_filename"].Should().Be(name);
        match.Reasons.Should().EndWith(filenameReason);
        match.Metadata.Keys.Should().Equal("xml_declaration", "encoding", "root_tag", "root_local_name", "config_original_filename", "config_role");
    }

    [Fact]
    public void ReleaseTransformFixtureRecordsStage()
    {
        var match = DetectFixture("config", "web.Release.config");

        match.Should().NotBeNull();
        match!.Variant.Should().Be("web-config-transform");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found <configuration> root element",
            "Matched known configuration section tags used by web frameworks",
            "Detected root element <configuration>",
            "Detected XML declaration",
            "XML version declared as 1.0",
            "XML declared encoding utf-8",
            "Recorded XML namespace declarations (xdt\u2192http://schemas.microsoft.com/XML-Document-Transform @L2)",
            "Filename pattern web|app.*.config suggests a build-specific transform",
            "Detected XML-Document-Transform namespace declaration (xdt)",
            "Found xdt:Transform attribute indicating config transform instructions",
            "Filename stage 'Release' indicates transform precedence");
        Entries(match.Metadata!["namespace_provenance"])[0]["column"].Should().Be(16);
        Strings(match.Metadata["config_transform_stages"]).Should().Equal("Release");
    }

    [Fact]
    public void MultiServerFixturesDetectResxAndProject()
    {
        var resx = DetectFixture("multi-server", "server01", "localization", "Strings.resx");
        resx.Should().NotBeNull();
        resx!.Variant.Should().Be("resource-xml");
        resx.Confidence.Should().BeApproximately(0.95, 1e-9);
        Strings(resx.Metadata!["resource_keys"]).Should().Equal("AppTitle", "WelcomeMessage");
        resx.Metadata["resource_keys_preview"].Should().Be("AppTitle, WelcomeMessage");
        resx.Reasons.Should().EndWith("Captured resource keys from .resx payload (e.g., AppTitle, WelcomeMessage)");
        Entries(resx.Metadata["namespace_provenance"])[0]["hash"].Should().Be("1a6387abd69c");

        var project = DetectFixture("multi-server", "server01", "msbuild", "Project.csproj");
        project.Should().NotBeNull();
        project!.Variant.Should().Be("msbuild-project");
        project.Confidence.Should().BeApproximately(0.925, 1e-9);
        project.Reasons.Should().Equal(
            "Found XML element structure",
            "Root element <Project> indicates an MSBuild project definition",
            "Detected root element <Project>",
            "MSBuild SDK specified (Microsoft.NET.Sdk)");
        project.Metadata!.Keys.Should().Equal(
            "root_tag", "root_local_name", "root_attributes", "msbuild_detected", "msbuild_kind", "msbuild_sdk", "xml_well_formed");

        var web = DetectFixture("multi-server", "server01", "web", "web.config");
        web.Should().NotBeNull();
        web!.Variant.Should().Be("web-config");
        web.Confidence.Should().BeApproximately(0.95, 1e-9);
    }
}
