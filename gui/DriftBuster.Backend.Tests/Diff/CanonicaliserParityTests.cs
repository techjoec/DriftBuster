using DriftBuster.Backend.Diff;

using static DriftBuster.Backend.Tests.Diff.DiffOracleData;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>
/// <see cref="Canonicaliser"/> against CPython's <c>canonicalise_text</c>, <c>canonicalise_json</c> and
/// <c>canonicalise_xml</c> on the adversarial inputs in <c>Data/canonicalise_cases.json</c>, plus the documented
/// divergences (namespace prefixes kept, entity declarations refused, interpreter errors not reproduced).
/// </summary>
public sealed class CanonicaliserParityTests
{
    private const string DataFile = "canonicalise_cases.json";

    public static TheoryData<string> ComparableCases
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var entry in Load(DataFile).Where(entry => !entry.ContainsKey("python_xml")))
            {
                names.Add((string)entry["name"]!);
            }

            return names;
        }
    }

    [Theory]
    [MemberData(nameof(ComparableCases))]
    public void CanonicalisersMatchPython(string name)
    {
        var entry = Case(DataFile, name);
        var payload = (string)entry["payload"]!;
        foreach (var contentType in Canonicaliser.ContentTypes)
        {
            var outcome = Map(entry[contentType]);
            if (outcome.TryGetValue("value", out var expected))
            {
                Canonicaliser.Canonicalise(payload, contentType).Should().Be((string)expected!, "content type {0}", contentType);
            }
        }
    }

    [Fact]
    public void XmlUnpairedSurrogateFallsBackToTextWherePythonRaises()
    {
        var entry = Case(DataFile, "text-lone-surrogate");
        Map(entry["xml"])["error"].Should().Be("UnicodeEncodeError");
        Canonicaliser.CanonicaliseXml("a\uD800b").Should().Be("a\uD800b");
    }

    [Fact]
    public void XmlNamespacePrefixesAreKeptAsWritten()
    {
        var entry = Case(DataFile, "xml-namespaces");
        var python = (string)Map(entry["python_xml"])["value"]!;
        python.Should().Be("<ns0:a xmlns:ns0=\"urn:p\" xmlns:ns1=\"urn:d\"><ns1:b y=\"2\" ns0:x=\"1\" /><c /></ns0:a>");

        Canonicaliser.CanonicaliseXml((string)entry["payload"]!).Should().Be(
            "<p:a xmlns=\"urn:d\" xmlns:p=\"urn:p\" xmlns:u=\"urn:unused\"><b y=\"2\" p:x=\"1\" /><c xmlns=\"\" /></p:a>");
    }

    [Fact]
    public void XmlQNameValuesKeepTheirBinding()
    {
        var payload = "<root xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:t='urn:types'><v xsi:type='t:Name' b=' '/></root>";
        Canonicaliser.CanonicaliseXml(payload).Should().Be(
            "<root xmlns:t=\"urn:types\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><v b=\"\" xsi:type=\"t:Name\" /></root>");
    }

    [Fact]
    public void XmlDefaultedNamespaceDeclarationIsWrittenOnTheElement()
    {
        var payload = "<!-- c --><!DOCTYPE a [<!ATTLIST a xmlns CDATA #FIXED 'urn:d' z CDATA 'dz'>]><a x='1'/>";
        Canonicaliser.CanonicaliseXml(payload).Should().Be("<a xmlns=\"urn:d\" x=\"1\" z=\"dz\" />");
    }

    [Theory]
    [InlineData("xml-entity-after-comment")]
    [InlineData("xml-entity-quoted-bracket")]
    public void XmlEntityDeclarationsAreRefusedWherePythonExpandsThem(string name)
    {
        var entry = Case(DataFile, name);
        var payload = (string)entry["payload"]!;
        Map(entry["python_xml"]).Should().ContainKey("value");
        Canonicaliser.CanonicaliseXml(payload).Should().Be(Canonicaliser.CanonicaliseText(payload));
    }

    [Fact]
    public void XmlNestingPastPythonRecursionLimitIsCanonicalised()
    {
        const int depth = 20000;
        var payload = string.Concat(Enumerable.Repeat("<a>", depth)) + " " + string.Concat(Enumerable.Repeat("</a>", depth));
        var expected = string.Concat(Enumerable.Repeat("<a>", depth - 1)) + "<a />" + string.Concat(Enumerable.Repeat("</a>", depth - 1));
        Canonicaliser.CanonicaliseXml(payload).Should().Be(expected);
    }

    [Fact]
    public void XmlTextOfManyReferencesAndInstructionsIsBuiltInLinearTime()
    {
        const int count = 200000;
        var payload = "<a>" + string.Concat(Enumerable.Repeat("x&amp;<?p?>", count)) + "<b/>" + string.Concat(Enumerable.Repeat("&#65;", count)) + "</a>";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var canonical = Canonicaliser.CanonicaliseXml(payload);
        watch.Stop();
        canonical.Should().Be("<a>" + string.Concat(Enumerable.Repeat("x&amp;", count)) + "<b />" + new string('A', count) + "</a>");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void JsonPastInterpreterLimitsFallsBackToText()
    {
        var bigInt = "[" + new string('7', 4301) + "]";
        Canonicaliser.CanonicaliseJson(bigInt).Should().Be(bigInt);

        var deep = new string('[', 9999) + new string(']', 9999);
        Canonicaliser.CanonicaliseJson(deep).Should().Be(deep);
    }

    [Fact]
    public void JsonAtDecoderDepthLimitSerialisesWithoutRecursion()
    {
        const int depth = 9998;
        var canonical = Canonicaliser.CanonicaliseJson(new string('[', depth) + new string(']', depth));
        canonical.Split('\n').Should().HaveCount((2 * depth) - 1);
        canonical.Should().StartWith("[\n  [\n    [");
    }

    [Fact]
    public void UnknownContentTypeRaises()
    {
        var act = () => Canonicaliser.Canonicalise("a", "yaml");
        act.Should().Throw<ArgumentException>().WithMessage("Unsupported content_type: yaml*");
    }
}
