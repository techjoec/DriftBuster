using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Catalog;

namespace DriftBuster.Backend.Tests.Detection;

/// <summary>The detection core types.</summary>
public sealed class TypesTests
{
    private static readonly DetectionCatalog Catalog = DetectionCatalog.Default;

    private static OrderedDictionary<string, object?> Meta(params (string Key, object? Value)[] pairs)
    {
        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            metadata[key] = value;
        }

        return metadata;
    }

    [Fact]
    public void ValidateDetectionMetadataAddsCatalogFields()
    {
        var match = new DetectionMatch("xml", "xml", "generic", 0.7, ["detected xml"], Meta(("bytes_sampled", 32)));

        var metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        metadata["catalog_version"].Should().Be(Catalog.Version);
        metadata["catalog_format"].Should().Be("xml");
        metadata["catalog_variant"].Should().Be("generic");
    }

    [Fact]
    public void ValidateDetectionMetadataRejectsUnknownFormat()
    {
        var match = new DetectionMatch("custom", "unknown-format", null, 0.2, []);

        var act = () => DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        act.Should().Throw<MetadataValidationError>().WithMessage("Unknown catalog format: unknown-format");
    }

    [Fact]
    public void SummariseMetadataSerialisesValues()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = new HashSet<string>(StringComparer.Ordinal) { "nested" } };
        var match = new DetectionMatch(
            "xml",
            "xml",
            "generic",
            0.9,
            ["detected"],
            Meta(("path", new FileInfo("/tmp/config.xml")), ("values", values)));
        match.Metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        var summary = DetectionMetadata.SummariseMetadata(match);

        summary["plugin"].Should().Be("xml");
        var metadata = summary["metadata"].Should().BeOfType<OrderedDictionary<string, object?>>().Subject;
        metadata["catalog_format"].Should().Be("xml");
        metadata["path"].Should().Be("/tmp/config.xml");
        var nested = metadata["values"].Should().BeOfType<OrderedDictionary<string, object?>>().Subject;
        nested["key"].Should().BeOfType<List<object?>>().Which.Should().Equal("nested");
    }

    [Fact]
    public void ValidateDetectionMetadataHandlesStrictFalse()
    {
        var match = new DetectionMatch(
            "custom",
            "Custom-Format",
            " CustomVariant ",
            0.5,
            [],
            Meta(("bytes", "data"u8.ToArray()), ("path", new FileInfo("/tmp/obj"))));

        var metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog, strict: false);

        // Variant is lowercased when strict is disabled and bytes become text.
        metadata["catalog_format"].Should().Be("custom-format");
        metadata["catalog_variant"].Should().Be("customvariant");
        metadata["bytes"].Should().Be("data");
        metadata["path"].Should().Be("/tmp/obj");
    }

    [Fact]
    public void ValidateDetectionMetadataResolvesAliasFormat()
    {
        var match = new DetectionMatch("dockerfile", "dockerfile", "generic", 0.8, ["Dockerfile heuristics matched"]);

        var metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        metadata["catalog_format"].Should().Be("script-config");
        metadata["catalog_variant"].Should().Be("generic");
    }

    [Fact]
    public void ValidateDetectionMetadataRejectsUnknownVariantForKnownFormat()
    {
        var match = new DetectionMatch("json", "json", "mystery", 0.4, ["unknown"]);

        var act = () => DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        act.Should().Throw<MetadataValidationError>().WithMessage("Unknown catalog variant 'mystery' for format 'json'.");
    }

    [Theory]
    [InlineData("nlog-config")]
    public void ValidateDetectionMetadataAcceptsStructuredXmlVendorVariants(string variant)
    {
        var match = new DetectionMatch("xml", "structured-config-xml", variant, 0.8, ["vendor logging config"]);

        var metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        metadata["catalog_format"].Should().Be("structured-config-xml");
        metadata["catalog_variant"].Should().Be(variant);
    }

    private static FormatClass Format(string name, string slug) => new(name, slug, 0, "low");

    private static DetectionCatalog SampleCatalog() => new(
        Version: "1.0.0",
        Updated: "",
        Classes: [Format("Json", "json"), Format("StructuredConfigXml", "structured-config-xml")],
        Fallback: new FallbackClass("unknown", "unknown-text-or-binary", 0, "low"));

    [Fact]
    public void ValidateDetectionMetadataStrictChecks()
    {
        var catalog = SampleCatalog();
        var match = new DetectionMatch("plugin", "unknown-format", null, 0.5, [], null);

        var strictAct = () => DetectionMetadata.ValidateDetectionMetadata(match, catalog);
        strictAct.Should().Throw<MetadataValidationError>();

        var relaxed = DetectionMetadata.ValidateDetectionMetadata(match, catalog, strict: false);
        relaxed["catalog_format"].Should().Be("unknown-format");

        var badFormat = new DetectionMatch("plugin", null!, null, 0.1, [], null);

        var badAct = () => DetectionMetadata.ValidateDetectionMetadata(badFormat, catalog);
        badAct.Should().Throw<MetadataValidationError>().WithMessage("DetectionMatch.format_name must be a string.");
    }
}
