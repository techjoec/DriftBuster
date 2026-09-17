using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The xml plugin's well-formedness flag.</summary>
public sealed class XmlWellformedFlagTests
{
    private static DetectionMatch? Detect(string name, string content)
        => new XmlPlugin().Detect(name, Encoding.UTF8.GetBytes(content), content);

    [Fact]
    public void XmlWellFormedTrueAndFalse()
    {
        const string ok = "<root><child/></root>";
        const string bad = "<root><child></root>"; // unbalanced

        var mOk = Detect("file.xml", ok);
        mOk.Should().NotBeNull();
        mOk!.Metadata.Should().NotBeNull();
        mOk.Metadata!["xml_well_formed"].Should().Be(true);

        var mBad = Detect("file.xml", bad);
        mBad.Should().NotBeNull();
        mBad!.Metadata.Should().NotBeNull();
        mBad.Metadata!["xml_well_formed"].Should().Be(false);
        mBad.Metadata["needs_review"].Should().Be(true);
        XmlPluginTests.Strings(mBad.Metadata["review_reasons"]).Should().NotBeEmpty();

        mOk.Confidence.Should().BeApproximately(0.73, 1e-9);
        mOk.Reasons.Should().Equal("File extension .xml suggests XML content", "Found XML element structure", "Detected root element <root>");
        mOk.Metadata.Keys.Should().Equal("root_tag", "root_local_name", "xml_well_formed");

        mBad.Confidence.Should().BeApproximately(0.73, 1e-9);
        mBad.Reasons.Should().Equal(
            "File extension .xml suggests XML content",
            "Found XML element structure",
            "XML appears not well-formed within sampled content",
            "Detected root element <root>");
        mBad.Metadata.Keys.Should().Equal("root_tag", "root_local_name", "xml_well_formed", "needs_review", "review_reasons");
        XmlPluginTests.Strings(mBad.Metadata["review_reasons"]).Should().Equal("XML not well-formed");
    }

    // Well-formedness probe verdicts on declarations, DTDs and entity references, where the plain .NET reader would decide differently.
    [Theory]
    [InlineData("\uFEFF<?xml version=\"1.0\"?><a><b/></a>", true)]
    [InlineData("<?xml version=\"1.0 \"?><r/>", false)]
    [InlineData("<?xml version=\"1.0\" encoding=\"8bit\"?><r/>", false)]
    [InlineData("<!DOCTYPE r SYSTEM \"x.dtd\"><r/>", true)]
    [InlineData("<!DOCTYPE r [<!ELEMENT r EMPTY>]><r/>", true)]
    [InlineData("<!DOCTYPE r [<!-- <!ENTITY x \"y\"> -->]><r/>", true)]
    [InlineData("<r><![CDATA[<!ENTITY x>]]></r>", true)]
    [InlineData("<!DOCTYPE r [<!ENTITY x \"y\">]><r>&x;</r>", false)]
    [InlineData("<!DOCTYPE r SYSTEM \"x.dtd\"><r a=\"&foo;\"/>", true)]
    [InlineData("<?xml version=\"1.0\" standalone=\"yes\"?><!DOCTYPE r SYSTEM \"x.dtd\"><r a=\"&foo;\"/>", false)]
    [InlineData("<!DOCTYPE r [%pe;]><r a=\"&foo;\"/>", true)]
    [InlineData("<!DOCTYPE r SYSTEM \"x\" [<!ATTLIST r a CDATA \"&x;\">]><r/>", true)]
    [InlineData("<!DOCTYPE r [<!ATTLIST r a CDATA \"<!ENTITY\">]><r/>", false)]
    [InlineData("<!DOCTYPE r [<!NOTATION n SYSTEM \"<!ENTITY x\">]><r/>", true)]
    [InlineData("<!DOCTYPE r [<!NOTATION n PUBLIC \"<!ENTITY x\">]><r/>", false)]
    [InlineData("<!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;\">]><lolz>&lol2;</lolz>", false)]
    [InlineData("<a/><b/>", false)]
    public void XmlWellFormedProbeVerdicts(string content, bool expected)
    {
        var match = Detect("probe.xml", content);
        match.Should().NotBeNull();
        match!.Metadata!["xml_well_formed"].Should().Be(expected);
        match.Metadata.ContainsKey("needs_review").Should().Be(!expected);
    }
}
