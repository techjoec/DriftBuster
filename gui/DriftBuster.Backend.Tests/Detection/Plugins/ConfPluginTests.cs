using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The conf plugin.</summary>
public sealed class ConfPluginTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-conf-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private const string Pipeline = """

            input {
              beats {
                port => 5044
              }
            }
            filter {
              mutate { add_field => { "[@metadata][index]" => "logs" } }
            }
            output {
              stdout { codec => rubydebug }
            }
            
        """;

    [Fact]
    public void LogstashPipelineDetection()
    {
        var plugin = new ConfPlugin();
        var match = plugin.Detect("logstash.conf", Encoding.UTF8.GetBytes(Pipeline), Pipeline);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("unix-conf");
        match.Variant.Should().Be("logstash-pipeline");
        match.Confidence.Should().BeGreaterThanOrEqualTo(0.72);
        match.Confidence.Should().BeApproximately(0.8, 1e-9);
        match.Reasons.Should().Equal(
            "Detected Logstash pipeline block(s): filter, input, output",
            "Found nested plugin stanza inside pipeline block");
        match.Metadata.Should().BeNull();
    }

    // The catalog lists a logstash-pipeline variant under unix-conf, so strict validation accepts this input.
    [Fact]
    public void DetectorStrictValidationAcceptsLogstashPipeline()
    {
        var target = Path.Combine(_tmp.FullName, "logstash.conf");
        File.WriteAllText(target, Pipeline, new UTF8Encoding(false));

        var detector = new Detector(plugins: [new ConfPlugin()], sortPlugins: false);
        var match = detector.ScanFile(target);

        match.Should().NotBeNull();
        match!.PluginName.Should().Be("conf");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["catalog_format"].Should().Be("unix-conf");
        match.Metadata["catalog_variant"].Should().Be("logstash-pipeline");
    }

    [Theory]
    [InlineData(30000, "    \n")]
    public void WhitespaceRunsAreScannedInLinearTime(int lines, string line)
    {
        var text = string.Concat(Enumerable.Repeat(line, lines)) + "input {\n  beats { port => 5044 }\n}\noutput {\n  stdout {}\n}\n";
        var started = System.Diagnostics.Stopwatch.StartNew();
        var match = new ConfPlugin().Detect("pipeline.conf", Encoding.UTF8.GetBytes(text), text);
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));

        match!.Variant.Should().Be("logstash-pipeline");
        match.Confidence.Should().BeApproximately(0.8, 1e-9);
        match.Reasons.Should().Equal(
            "Detected Logstash pipeline block(s): input, output",
            "Found nested plugin stanza inside pipeline block");
    }
}
