using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The hcl plugin.</summary>
public sealed class HclPluginTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-hcl-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private static DetectionMatch? Detect(string name, string content)
        => new HclPlugin().Detect(name, Encoding.UTF8.GetBytes(content), content);

    private const string Nomad = """

            job "example" {
              datacenters = ["dc1"]
              group "g" {
                task "t" { driver = "docker" }
              }
            }
            
        """;

    [Fact]
    public void HclNomadDetection()
    {
        var match = Detect("client.hcl", Nomad);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("hcl");
        match.Variant.Should().Be("hashicorp-nomad");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .hcl suggests HashiCorp HCL",
            "Found HCL-style block declarations (e.g., job/server)",
            "Detected key = value assignments");
        match.Metadata!.Keys.Should().Equal("blocks_preview");
        YamlPluginTests.Strings(match.Metadata["blocks_preview"]).Should().Equal("job");
    }

    [Fact]
    public void HclConsulDetection()
    {
        const string consul = """

                server {
                  enabled = true
                }
                datacenter = "dc1"
                
            """;
        var match = Detect("consul.hcl", consul);
        match.Should().NotBeNull();
        match!.Variant.Should().BeOneOf("hashicorp-consul", "generic");
        match.Variant.Should().Be("hashicorp-consul");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .hcl suggests HashiCorp HCL",
            "Found HCL-style block declarations (e.g., job/server)",
            "Detected key = value assignments");
        YamlPluginTests.Strings(match.Metadata!["blocks_preview"]).Should().Equal("server");
    }

    // The catalog maps "hcl" to its own class with a hashicorp-nomad variant, so strict validation accepts this input.
    [Fact]
    public void DetectorStrictValidationAcceptsNomadJob()
    {
        var target = Path.Combine(_tmp.FullName, "client.hcl");
        File.WriteAllText(target, Nomad, new UTF8Encoding(false));

        var detector = new Detector(plugins: [new HclPlugin()], sortPlugins: false);
        var match = detector.ScanFile(target);

        match.Should().NotBeNull();
        match!.PluginName.Should().Be("hcl");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["catalog_format"].Should().Be("hcl");
        match.Metadata["catalog_variant"].Should().Be("hashicorp-nomad");
    }

    [Theory]
    [InlineData(30000, "    \n")]
    public void WhitespaceRunsAreScannedInLinearTime(int lines, string line)
    {
        var text = string.Concat(Enumerable.Repeat(line, lines)) + "server {\n  bind = \"0.0.0.0\"\n}\nkey = value\n";
        var started = System.Diagnostics.Stopwatch.StartNew();
        var match = Detect("run.hcl", text);
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));

        match!.Variant.Should().Be("hashicorp-consul");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "File extension .hcl suggests HashiCorp HCL",
            "Found HCL-style block declarations (e.g., job/server)",
            "Detected key = value assignments");
        match.Metadata!["blocks_preview"].Should().BeAssignableTo<IEnumerable<string>>().Subject.Should().Equal("server");
    }
}
