using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>
/// Mirror of tests/formats/test_xml_plugin_defused.py. Python swaps <c>DEFUSED_ET</c> for a fake whose <c>fromstring</c>
/// delegates to the plain parser; the mirror swaps the <see cref="XmlPlugin.DefusedFromString"/> seam the same way.
/// </summary>
public sealed class XmlPluginDefusedTests
{
    [Fact]
    public void CollectMetadataUsesDefusedxmlBranch()
    {
        // Build a simple XML payload that is safe to parse and includes declaration + root
        const string xmlText = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root attr=\"v\" xmlns=\"urn:test\">value</root>\n";

        var calls = new List<string>();
        var plugin = new XmlPlugin
        {
            DefusedAvailable = true,
            DefusedFromString = text =>
            {
                calls.Add(text);
                return DefusedXmlParser.ParseTree(text);
            },
        };

        var md = plugin.CollectMetadata(xmlText, ".xml");

        // The fake stood in for the defusedxml parser exactly once, with the stripped payload.
        calls.Should().Equal(xmlText);

        XmlPluginTests.AssertMapping(md["xml_declaration"], ("version", "1.0"), ("encoding", "utf-8"));
        md["root_tag"].Should().Be("root");
        md["root_local_name"].Should().Be("root");
        md["root_namespace"].Should().Be("urn:test");
        // When parsing succeeded, msbuild hints should not be injected spuriously
        md.Should().NotContainKey("msbuild_detected");

        md.Keys.Should().Equal(
            "xml_declaration", "encoding", "root_tag", "root_local_name", "root_attributes", "namespaces", "namespace_provenance", "root_namespace");
        XmlPluginTests.AssertMapping(md["root_attributes"], ("attr", "v"), ("xmlns", "urn:test"));
        XmlPluginTests.AssertMapping(
            XmlPluginTests.Entries(md["namespace_provenance"])[0],
            ("attribute", "xmlns"),
            ("prefix", null),
            ("uri", "urn:test"),
            ("line", 2),
            ("column", 16),
            ("source", "root-attribute"),
            ("hash", "c66ab5ef1924"));
    }

    [Theory]
    [InlineData("<!DOCTYPE root><root xmlns=\"http://schemas.microsoft.com/resx/2005\"><data name=\"Key\" /></root>")]
    [InlineData("<!DOCTYPE root [<!ENTITY x \"y\">]><root xmlns=\"http://schemas.microsoft.com/resx/2005\"><data name=\"Key\" />&x;</root>")]
    [InlineData("<root xmlns=\"http://schemas.microsoft.com/resx/2005\"><!ENTITY x \"y\"><data name=\"Key\" /></root>")]
    public void DoctypeAndEntityPayloadsSkipTheTreeWalk(string payload)
    {
        // The guard refuses the parse before the reader sees the DOCTYPE, so only the regex-derived keys appear.
        var md = new XmlPlugin().CollectMetadata(payload, ".resx");
        md.Should().NotContainKey("resource_keys");
        md.Should().NotContainKey("attribute_hints");
        md["root_local_name"].Should().Be("root");
        md["root_namespace"].Should().Be("http://schemas.microsoft.com/resx/2005");
    }
}
