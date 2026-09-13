using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>Plan fix f: the diff content type comes from detection, never from the file extension.</summary>
[Collection(DiffSafetyLimitsCollection.Name)]
public sealed class ContentTypeResolverTests : IDisposable
{
    private const string XmlSettings = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <appSettings>\n    <add key=\"b\" value=\"2\" a=\"1\" />\n  </appSettings>\n</configuration>\n";

    private const string AppSettingsJson = "{\n  \"Logging\": {\"LogLevel\": {\"Default\": \"Information\"}},\n  \"AllowedHosts\": \"*\"\n}\n";

    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-content-type-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_tmp.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("structured-config-xml", "xml")]
    [InlineData("xml", "xml")]
    [InlineData("json", "text")]
    [InlineData("ini", "text")]
    [InlineData("XML", "text")]
    [InlineData(null, "text")]
    public void FromCatalogFormatFollowsTheMultiServerRule(string? catalogFormat, string expected)
    {
        ContentTypeResolver.FromCatalogFormat(catalogFormat).Should().Be(expected);
    }

    [Fact]
    public void FromMatchWithoutMetadataIsText()
    {
        ContentTypeResolver.FromMatch(null).Should().Be("text");
        ContentTypeResolver.FromMatch(new DetectionMatch("text", "text", null, 0.5, [], null)).Should().Be("text");
    }

    [Fact]
    public void XmlContentInSettingsTxtCanonicalisesAsXml()
    {
        var baseline = Write("settings.txt", XmlSettings);
        var candidate = Write("settings-copy.txt", XmlSettings.Replace("value=\"2\"", "value=\"3\"", StringComparison.Ordinal));

        ContentTypeResolver.ResolveFile(baseline).Should().Be("xml");
        var contentType = ContentTypeResolver.ResolvePair(baseline, candidate);
        contentType.Should().Be("xml");

        var artifact = DiffBuilder.BuildUnifiedDiff(File.ReadAllText(baseline), File.ReadAllText(candidate), contentType);
        artifact.CanonicalBefore.Should().Be(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><appSettings><add a=\"1\" key=\"b\" value=\"2\" /></appSettings></configuration>");
    }

    [Fact]
    public void AppSettingsJsonIsDiffedAsText()
    {
        var baseline = Write("appsettings.json", AppSettingsJson);

        new Detector().ScanFile(baseline)!.Metadata!["catalog_format"].Should().Be("json");
        ContentTypeResolver.ResolveFile(baseline).Should().Be("text");
        ContentTypeResolver.ResolvePair(baseline, baseline).Should().Be("text");
        DiffBuilder.BuildUnifiedDiff(AppSettingsJson, AppSettingsJson, ContentTypeResolver.ResolvePair(baseline, baseline))
            .CanonicalBefore.Should().Be(Canonicaliser.CanonicaliseText(AppSettingsJson));
    }

    [Fact]
    public void AllowlistedExtensionWithoutXmlContentIsText()
    {
        var baseline = Write("app.csproj", "alpha = 1\nbeta = 2\n");
        var candidate = Write("app2.csproj", "alpha = 1\nbeta = 3\n");

        ContentTypeResolver.ResolvePair(baseline, candidate).Should().Be("text");
    }

    [Fact]
    public void EitherSideDetectedAsXmlSelectsXml()
    {
        var xml = Write("settings.txt", XmlSettings);
        var plain = Write("notes.txt", "just some notes\n");

        ContentTypeResolver.ResolvePair(plain, xml).Should().Be("xml");
        ContentTypeResolver.ResolvePair(xml, plain).Should().Be("xml");
    }

    [Fact]
    public void MissingFileResolvesToText()
    {
        ContentTypeResolver.ResolveFile(Path.Combine(_tmp.FullName, "missing.xml")).Should().Be("text");
    }
}
