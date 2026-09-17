using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Catalog;

namespace DriftBuster.Backend.Tests.Detection;

/// <summary>Detection catalog metadata, including the plist, markdown-config, logstash-pipeline and hcl entries.</summary>
public sealed class CatalogTests
{
    private static readonly DetectionCatalog Catalog = DetectionCatalog.Default;

    private static List<OrderedDictionary<string, object?>> Remediations(OrderedDictionary<string, object?> metadata)
        => metadata["catalog_remediations"].Should().BeOfType<List<object?>>().Subject
            .Select(entry => entry.Should().BeOfType<OrderedDictionary<string, object?>>().Subject)
            .ToList();

    [Fact]
    public void CatalogInjectsSeverityHintAndRemediations()
    {
        var match = new DetectionMatch("registry", "registry-export", null, 0.9, ["synthetic"]);

        var metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        metadata["catalog_severity"].Should().Be("high");
        metadata["catalog_severity_hint"].Should().BeOfType<string>().Which.Should().StartWith("Registry exports capture");
        var remediations = Remediations(metadata);
        remediations.Should().NotBeEmpty();
        var references = metadata["catalog_references"].Should().BeOfType<List<object?>>().Subject;
        references.Should().Contain("docs/detection-types.md#registryexport");
        remediations.Should().Contain(entry =>
            Equals(entry["id"], "registry-export-lockdown") && Equals(entry["category"], "secrets"));
    }

    [Fact]
    public void VariantSpecificSeverityOverridesDefault()
    {
        var match = new DetectionMatch("conf", "unix-conf", "generic-directive-text", 0.5, ["synthetic"]);

        var metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        metadata["catalog_format"].Should().Be("unix-conf");
        metadata["catalog_variant"].Should().Be("generic-directive-text");
        metadata["catalog_severity"].Should().Be("medium");
        metadata["catalog_severity_hint"].Should().BeOfType<string>().Which.Should().StartWith("Unix configuration files");
        var catalogRemediations = Remediations(metadata);
        var references = metadata["catalog_references"].Should().BeOfType<List<object?>>().Subject;
        references.Should().Contain("docs/detection-types.md#unixconf");
        catalogRemediations.Should().Contain(entry => Equals(entry["id"], "unix-conf-hardening"));
    }

    [Fact]
    public void VariantSpecificRemediationHintsExtendBase()
    {
        var match = new DetectionMatch("ini", "ini", "dotenv", 0.8, ["synthetic"]);

        var metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        metadata["catalog_severity"].Should().Be("high");
        metadata["catalog_severity_hint"].Should().BeOfType<string>().Which.Should().StartWith("Dotenv environment files");
        var remediations = Remediations(metadata);
        var remediationIds = remediations.Select(entry => entry["id"]).ToHashSet();
        remediationIds.Should().Contain("ini-secret-rotation");
        remediationIds.Should().Contain("ini-dotenv-rotate-secrets");
        remediations.Should().Contain(entry =>
            entry.ContainsKey("documentation") && Equals(entry["documentation"], "docs/detection-types.md#ini-dotenv"));
    }

    // Plugin outputs validate under strict validation.

    [Theory]
    [InlineData("binary", "plist", "xml-or-binary", "plist", false)]
    [InlineData("binary", "markdown-config", "embedded-yaml-frontmatter", "markdown-config", false)]
    [InlineData("conf", "unix-conf", "logstash-pipeline", "unix-conf", true)]
    [InlineData("hcl", "hcl", "hashicorp-nomad", "hcl", false)]
    [InlineData("hcl", "hcl", "hashicorp-vault", "hcl", false)]
    [InlineData("hcl", "hcl", "hashicorp-consul", "hcl", false)]
    [InlineData("hcl", "hcl", "generic", "hcl", false)]
    public void StrictValidationAcceptsPortedPluginOutputs(string plugin, string format, string variant, string expectedFormat, bool hasReferences)
    {
        var match = new DetectionMatch(plugin, format, variant, 0.8, ["synthetic"]);

        var metadata = DetectionMetadata.ValidateDetectionMetadata(match, Catalog);

        metadata["catalog_format"].Should().Be(expectedFormat);
        metadata["catalog_variant"].Should().Be(variant);
        metadata.Should().ContainKey("catalog_severity");
        if (hasReferences)
        {
            metadata.Should().ContainKey("catalog_references", "the variant joins an existing documented class");
        }
        else
        {
            metadata.Should().NotContainKey("catalog_references", "no documentation anchor exists for the added classes");
        }
    }

    [Fact]
    public void HclIsItsOwnClassAndNoLongerAnIniAlias()
    {
        var lookup = DetectionMetadata.BuildFormatLookup(Catalog);

        lookup["hcl"].Canonical.Should().Be("hcl");
        lookup["hcl"].Variants.Should().BeEquivalentTo(["generic", "hashicorp-nomad", "hashicorp-vault", "hashicorp-consul"]);
        Catalog.Classes.Single(format => string.Equals(format.Slug, "ini", StringComparison.Ordinal)).Aliases.Should().NotContain("hcl");
        Catalog.Classes.Select(format => format.Priority).Should().BeInAscendingOrder();
    }

    [Fact]
    public void FormatLookupExposesSlugNameAndAliasKeys()
    {
        var lookup = DetectionMetadata.BuildFormatLookup(Catalog);

        Catalog.Version.Should().Be("0.0.3");
        lookup["registry-export"].Canonical.Should().Be("registry-export");
        lookup["registryexport"].Canonical.Should().Be("registry-export");
        lookup["structured-config"].Canonical.Should().Be("structured-config-xml");
        lookup["xml-generic"].Canonical.Should().Be("xml");
        lookup["env-file"].Canonical.Should().Be("ini");
        lookup["ini-json-hybrid"].Canonical.Should().Be("ini");
        lookup["dockerfile"].Canonical.Should().Be("script-config");
        lookup["sqlite"].Canonical.Should().Be("embedded-sql-db");
        lookup["binary"].Canonical.Should().Be("binary-dat");
        lookup["unknown-text-or-binary"].Canonical.Should().Be("unknown-text-or-binary");
        lookup["unknown-text-or-binary"].Variants.Should().BeEmpty();
        lookup["unknowntextorbinary"].Canonical.Should().Be("unknown-text-or-binary");
        lookup["ini"].Variants.Should().BeEquivalentTo(
            ["sectioned-ini", "sectionless-ini", "desktop-ini", "section-json-hybrid", "dotenv", "env", "env-file", "java-properties"]);
        lookup["structured-config-xml"].Variants.Should().Contain("sample");
    }
}
