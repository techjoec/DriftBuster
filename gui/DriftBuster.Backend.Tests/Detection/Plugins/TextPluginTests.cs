using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The text plugin and its review flags.</summary>
public sealed class TextPluginTests
{
    private static DetectionMatch? Detect(string name, string content)
    {
        var plugin = new TextPlugin();
        return plugin.Detect(name, Encoding.UTF8.GetBytes(content), content);
    }

    [Fact]
    public void OpensshSshdConfigDetection()
    {
        const string content = """

            # OpenSSH server config
            Port 22
            PermitRootLogin no
            PasswordAuthentication yes
            Subsystem sftp C:/Windows/System32/OpenSSH/sftp-server.exe

        """;
        var match = Detect("sshd_config", content);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("unix-conf");
        match.Variant.Should().Be("openssh-conf");
        match.Confidence.Should().BeApproximately(0.8, 1e-9);
        match.Reasons.Should().Equal(
            "Detected whitespace-delimited directives with minimal assignments",
            "Found comment lines typical of text configs",
            "Matched OpenSSH markers (sshd_config or Subsystem sftp)");
        match.Metadata.Should().NotBeNull();
        // The Subsystem line carries ':' in its path, so it counts as an assignment, not a directive.
        match.Metadata!["directive_lines"].ShouldBeJson(3);
        match.Metadata["comment_lines"].ShouldBeJson(1);
        match.Metadata.Should().NotContainKey("needs_review");
    }

    [Fact]
    public void OpenvpnClientConfDetection()
    {
        const string content = """

            client
            dev tun
            proto udp
            remote vpn.example.com 1194
            resolv-retry infinite
            nobind
            persist-key
            persist-tun

        """;
        var match = Detect("client.conf", content);
        match.Should().NotBeNull();
        match!.Variant.Should().Be("openvpn-conf");
        match.Confidence.Should().BeApproximately(0.8, 1e-9);
        match.Metadata!["directive_lines"].ShouldBeJson(8);
        match.Metadata.Should().NotContainKey("comment_lines");
    }

    [Fact]
    public void TextPluginIgnoresAssignmentHeavyFiles()
    {
        const string content = """

            key1=value1
            key2=value2
            key3=value3
            key4=value4

        """;
        Detect("assignments.conf", content).Should().BeNull();
    }

    // Review flags
    [Fact]
    public void TextMarkerFlag()
    {
        var content = """
            client
            dev tun
            remote 1.2.3.4 1194
            <<<DRIFT>>>
            """.Trim();
        var match = Detect("client.conf", content);
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["needs_review"].ShouldBeJson(true);
        var reviewReasons = BinaryPluginTests.Strings(match.Metadata["review_reasons"]);
        reviewReasons.Should().Contain(reason => reason.Contains("Nonstandard marker", StringComparison.Ordinal));
    }

    [Fact]
    public void GenericDirectiveTextGetsBaseConfidence()
    {
        const string content = "alpha one\nbeta two\ngamma three\ndelta four\n";
        var match = Detect("plain.conf", content);
        match.Should().NotBeNull();
        match!.Variant.Should().Be("generic-directive-text");
        match.Confidence.Should().Be(0.68);
        match.Reasons.Should().Equal("Detected whitespace-delimited directives with minimal assignments");
        match.Metadata!.Keys.Should().Equal("directive_lines");
    }

    [Fact]
    public void OpensshHintRelaxesDirectiveGate()
    {
        const string content = "Port 22\nPermitRootLogin no\nSubsystem sftp internal-sftp\nkey=value\nother=value\n";
        var match = Detect("custom.conf", content);
        match.Should().NotBeNull();
        match!.Variant.Should().Be("openssh-conf");
        match.Metadata!["directive_lines"].ShouldBeJson(3);
    }
}
