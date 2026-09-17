using DriftBuster.Backend.Diff;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>XML canonicalisation.</summary>
public sealed class CanonicaliseXmlTests
{
    [Fact]
    public void CanonicaliseXmlNormalisesStructureAndPreservesProlog()
    {
        var payload =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<!DOCTYPE note [\n"
            + "<!ELEMENT note ANY>\n"
            + "]>\n"
            + "<note  b=\"2\"   a=\"1\">\n"
            + "    <child other=\"two\" attr=\" value \">  spaced text  </child>  \n\n"
            + "    <selfclosing   beta=\"b\"    alpha=\"a\"/>\n"
            + "</note>\n";

        var expected =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<!DOCTYPE note [\n"
            + "<!ELEMENT note ANY>\n"
            + "]>\n"
            + "<note a=\"1\" b=\"2\"><child attr=\" value \" other=\"two\">  spaced text  </child>"
            + "<selfclosing alpha=\"a\" beta=\"b\" /></note>";

        Canonicaliser.CanonicaliseXml(payload).Should().Be(expected);
    }

    [Fact]
    public void CanonicaliseXmlCleansWhitespaceOnlyNodesAndSortsAttributes()
    {
        var payload =
            "<root attr=' padded ' other='value'>\n"
            + "    <empty>   </empty>   \n"
            + "    <node b='2' a='1'>value</node>\n"
            + "</root>\n";

        var expected = "<root attr=\" padded \" other=\"value\"><empty /><node a=\"1\" b=\"2\">value</node></root>";

        Canonicaliser.CanonicaliseXml(payload).Should().Be(expected);
    }

    [Theory]
    [InlineData("<root><unclosed></root>")]
    public void CanonicaliseXmlFallsBackToTextOnParseError(string payload)
    {
        Canonicaliser.CanonicaliseXml(payload).Should().Be(Canonicaliser.CanonicaliseText(payload));
    }

    [Fact]
    public void CanonicaliseXmlStripsBomPrefix()
    {
        var payload = "\uFEFF<?xml version='1.0'?><root> value </root>";
        var result = Canonicaliser.CanonicaliseXml(payload);
        result.Should().NotStartWith("\uFEFF");
        result.Should().Be("<?xml version='1.0'?>\n<root> value </root>");
    }
}
