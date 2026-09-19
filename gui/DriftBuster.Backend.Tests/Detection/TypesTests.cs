using System.Text.Json.Nodes;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Catalog;

namespace DriftBuster.Backend.Tests.Detection;

/// <summary>The detection core types.</summary>
public sealed class TypesTests
{
    private static readonly DetectionCatalog Catalog = DetectionCatalog.Default;

    [Fact]
    public void ValidateDetectionMetadataAddsCatalogFields()
    {
        var match = new DetectionMatch("xml", "xml", "generic", 0.7, ["detected xml"], new JsonObject { ["bytes_sampled"] = 32 });

        var metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        metadata["catalog_version"].ShouldBeJson(Catalog.Version);
        metadata["catalog_format"].ShouldBeJson("xml");
        metadata["catalog_variant"].ShouldBeJson("generic");
    }

    [Fact]
    public void ValidateDetectionMetadataRejectsUnknownFormat()
    {
        var match = new DetectionMatch("custom", "unknown-format", null, 0.2, []);

        var act = () => DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        act.Should().Throw<MetadataValidationException>().WithMessage("Unknown catalog format: unknown-format");
    }

    [Fact]
    public void Validation_enriches_the_match_in_place_and_a_payload_is_a_copy()
    {
        var match = new DetectionMatch("xml", "xml", "generic", 0.9, ["detected"], new JsonObject { ["values"] = new JsonArray("nested") });

        var metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog);
        var payload = match.ToPayload("/tmp/config.xml");
        match.Metadata["values"]!.AsArray().Add("later");

        metadata.Should().BeSameAs(match.Metadata);
        payload.Should().BeEquivalentTo(new { Path = "/tmp/config.xml", Plugin = "xml", Format = "xml", Variant = "generic", Confidence = 0.9, Reasons = new[] { "detected" } });
        payload.Metadata.Text("catalog_format").Should().Be("xml");
        payload.Metadata["values"].ShouldBeJson(new[] { "nested" });
    }

    [Fact]
    public void ValidateDetectionMetadataHandlesStrictFalse()
    {
        var match = new DetectionMatch(
            "custom",
            "Custom-Format",
            " CustomVariant ",
            0.5,
            []);

        var metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog, strict: false);

        // Variant is lowercased when strict is disabled.
        metadata["catalog_format"].ShouldBeJson("custom-format");
        metadata["catalog_variant"].ShouldBeJson("customvariant");
    }

    [Fact]
    public void ValidateDetectionMetadataResolvesAliasFormat()
    {
        var match = new DetectionMatch("dockerfile", "dockerfile", "generic", 0.8, ["Dockerfile heuristics matched"]);

        var metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        metadata["catalog_format"].ShouldBeJson("script-config");
        metadata["catalog_variant"].ShouldBeJson("generic");
    }

    [Fact]
    public void ValidateDetectionMetadataRejectsUnknownVariantForKnownFormat()
    {
        var match = new DetectionMatch("json", "json", "mystery", 0.4, ["unknown"]);

        var act = () => DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        act.Should().Throw<MetadataValidationException>().WithMessage("Unknown catalog variant 'mystery' for format 'json'.");
    }

    [Theory]
    [InlineData("nlog-config")]
    public void ValidateDetectionMetadataAcceptsStructuredXmlVendorVariants(string variant)
    {
        var match = new DetectionMatch("xml", "structured-config-xml", variant, 0.8, ["vendor logging config"]);

        var metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        metadata["catalog_format"].ShouldBeJson("structured-config-xml");
        metadata["catalog_variant"].ShouldBeJson(variant);
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
        strictAct.Should().Throw<MetadataValidationException>();

        var relaxed = DetectionMetadata.ValidateDetectionMetadata(match, catalog, strict: false);
        relaxed["catalog_format"].ShouldBeJson("unknown-format");

        var badFormat = new DetectionMatch("plugin", null!, null, 0.1, [], null);

        var badAct = () => DetectionMetadata.ValidateDetectionMetadata(badFormat, catalog);
        badAct.Should().Throw<MetadataValidationException>().WithMessage("DetectionMatch.format_name must be a string.");
    }
}
