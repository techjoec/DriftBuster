using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>Mirror of tests/formats/test_dockerfile_plugin.py; expected values were read from the Python plugin.</summary>
public sealed class DockerfilePluginTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-dockerfile-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private static DetectionMatch? Detect(string name, string content)
        => new DockerfilePlugin().Detect(name, Encoding.UTF8.GetBytes(content), content);

    private const string Sample = """

            # base image
            FROM python:3.11-slim
            RUN pip install -U pip
            COPY . /app
            WORKDIR /app
            
        """;

    [Fact]
    public void DockerfileDetection()
    {
        var match = Detect("Dockerfile", Sample);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("dockerfile");
        match.Variant.Should().Be("generic");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Filename suggests a Dockerfile",
            "First non-comment line starts with FROM",
            "Found common Dockerfile directives (RUN/COPY/ARG)");
        match.Metadata.Should().BeNull();
    }

    // The Python catalog resolves "dockerfile" through the script-config alias as well; this pins the port to the
    // same strict-validation outcome through a real Detector.
    [Fact]
    public void DetectorStrictValidationAcceptsDockerfile()
    {
        var target = Path.Combine(_tmp.FullName, "Dockerfile");
        File.WriteAllText(target, Sample, new UTF8Encoding(false));

        var detector = new Detector(plugins: [new DockerfilePlugin()], sortPlugins: false);
        var match = detector.ScanFile(target);

        match.Should().NotBeNull();
        match!.PluginName.Should().Be("dockerfile");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["catalog_format"].Should().Be("script-config");
        match.Metadata["catalog_variant"].Should().Be("generic");
    }

    // Python re-scans the run from every line start (quadratic); the port skips each whitespace run once.
    [Theory]
    [InlineData(50000, "\n")]
    [InlineData(30000, "    \n")]
    public void WhitespaceRunsAreScannedInLinearTime(int lines, string line)
    {
        var text = string.Concat(Enumerable.Repeat(line, lines)) + "FROM python:3.11\nRUN echo hi\n";
        var started = System.Diagnostics.Stopwatch.StartNew();
        var match = Detect("run.dockerfile", text);
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));

        match!.Variant.Should().Be("generic");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Filename suggests a Dockerfile",
            "First non-comment line starts with FROM",
            "Found common Dockerfile directives (RUN/COPY/ARG)");
    }
}
