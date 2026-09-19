using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The XML plugin's tree-derived metadata over <c>SafeXml</c>: DOCTYPEs are skipped, declared entities never expand.</summary>
public sealed class XmlPluginTreeTests
{
    private const string ResxNamespace = "http://schemas.microsoft.com/resx/2005";

    [Fact]
    public void A_doctype_without_entity_use_still_yields_tree_metadata()
    {
        var md = new XmlPlugin().CollectMetadata($"<!DOCTYPE root><root xmlns=\"{ResxNamespace}\"><data name=\"Key\" /></root>", ".resx");

        md["doctype"].Should().Be("root");
        md["resource_keys"].Should().BeEquivalentTo(new[] { "Key" });
    }

    [Theory]
    [InlineData($"<!DOCTYPE root [<!ENTITY x \"y\">]><root xmlns=\"{ResxNamespace}\"><data name=\"Key\" />&x;</root>")]
    [InlineData($"<!DOCTYPE root SYSTEM \"http://example.invalid/evil.dtd\"><root xmlns=\"{ResxNamespace}\"><data name=\"Key\" />&ext;</root>")]
    [InlineData($"<root xmlns=\"{ResxNamespace}\"><!ENTITY x \"y\"><data name=\"Key\" /></root>")]
    public void Entity_use_or_misplaced_declarations_leave_the_tree_unparsed(string payload)
    {
        var md = new XmlPlugin().CollectMetadata(payload, ".resx");

        md.Should().NotContainKey("resource_keys");
        md.Should().NotContainKey("attribute_hints");
        md["root_local_name"].Should().Be("root");
        md["root_namespace"].Should().Be(ResxNamespace);
    }
}
