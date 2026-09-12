using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>Mirror of tests/formats/test_text_plugin.py and tests/formats/test_text_flags.py.</summary>
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
        match.Metadata!["directive_lines"].Should().Be(3);
        match.Metadata["comment_lines"].Should().Be(1);
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
        match.Metadata!["directive_lines"].Should().Be(8);
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

    // tests/formats/test_text_flags.py
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
        match.Metadata!["needs_review"].Should().Be(true);
        var reviewReasons = match.Metadata["review_reasons"].Should().BeAssignableTo<IEnumerable<string>>().Subject;
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
        match.Metadata!["directive_lines"].Should().Be(3);
    }

    // Python runs ^\s*Subsystem\s+sftp\b with MULTILINE over "\n".join(lines), so "\s+" may swallow the line break:
    // "Subsystem" on one line and "sftp" at the start of the next (blank lines between allowed) still set the hint.
    [Fact]
    public void SubsystemSplitAcrossLinesSetsOpensshHint()
    {
        var split = Detect("split.conf", "Port 22\nSubsystem\nsftp /usr/lib/openssh/sftp-server\nPermitRootLogin no\n");
        split!.Variant.Should().Be("openssh-conf");
        split.Confidence.Should().BeApproximately(0.8, 1e-9);
        split.Reasons.Should().Contain("Matched OpenSSH markers (sshd_config or Subsystem sftp)");

        var blank = Detect("blank.conf", "Port 22\nSubsystem  \n\n \n\tsftp internal-sftp\nX11Forwarding no\n");
        blank!.Variant.Should().Be("openssh-conf");

        // With the hint, three directives are enough; without it (no "sftp" on the following line) they are not.
        Detect("three.conf", "Subsystem\nsftp internal-sftp\nPort 22\n")!.Metadata!["directive_lines"].Should().Be(3);
        Detect("gap.conf", "Subsystem\nPort 22\nsftp internal-sftp\n").Should().BeNull();
        Detect("joined.conf", "Subsystemsftp\nPort 22\nX11Forwarding no\n").Should().BeNull();
        Detect("last.conf", "Port 22\nX11Forwarding no\nSubsystem\n").Should().BeNull();
        // "sftp\b": a word character after sftp on the next line breaks the marker.
        Detect("word.conf", "Subsystem\nsftpd internal\nPort 22\n").Should().BeNull();
    }

    [Fact]
    public void ThreeDirectivesWithoutHintsDoNotMatch()
    {
        Detect("plain.conf", "alpha one\nbeta two\ngamma three\n").Should().BeNull();
    }

    [Fact]
    public void CountsCapAtFiftyAndLinesBeyondFiveHundredAreIgnored()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 120; index++)
        {
            builder.Append("# comment ").Append(index).Append('\n');
            builder.Append("directive ").Append(index).Append('\n');
        }

        var match = Detect("big.conf", builder.ToString());
        match!.Metadata!["directive_lines"].Should().Be(50);
        match.Metadata["comment_lines"].Should().Be(50);

        var late = new StringBuilder();
        for (var index = 0; index < 500; index++)
        {
            late.Append("key=value\n");
        }

        late.Append("alpha one\nbeta two\ngamma three\ndelta four\n");
        Detect("late.conf", late.ToString()).Should().BeNull();
    }

    [Fact]
    public void MarkerBeyondFirstHundredLinesIsNotFlagged()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 100; index++)
        {
            builder.Append("directive ").Append(index).Append('\n');
        }

        builder.Append("<<<DRIFT>>>\n");
        var match = Detect("late-marker.conf", builder.ToString());
        match!.Metadata.Should().NotContainKey("needs_review");
    }

    [Fact]
    public void SplitsOnPythonLineBoundaries()
    {
        const string content = "alpha one\rbeta two\x0cgamma three\u2028delta four";
        var match = Detect("mixed.conf", content);
        match.Should().NotBeNull();
        match!.Metadata!["directive_lines"].Should().Be(4);
    }

    // Python str.strip()/re \s treat U+001F as whitespace; it survives splitlines (U+001C-U+001E do not).
    [Fact]
    public void UnitSeparatorIsWhitespaceLikePython()
    {
        Detect("prefixed.conf", "\u001Falpha one\n\u001Fbeta two\n\u001Fgamma three\n\u001Fdelta four\n")!
            .Metadata!["directive_lines"].Should().Be(4);
        Detect("x1f.conf", "Port 22\nPermitRootLogin no\nSubsystem\u001Fsftp internal\nUsePAM yes\n")!
            .Variant.Should().Be("openssh-conf");
        var flagged = Detect("marker.conf", "alpha one\nbeta two\ngamma three\ndelta four\n\u001F<<<MARK>>>\n");
        flagged!.Metadata!["needs_review"].Should().Be(true);
    }

    // Python \b uses \w = [L N _]: a superscript digit (No) continues the word, a combining mark (Mn) ends it.
    [Fact]
    public void WordBoundaryUsesPythonWordCharacters()
    {
        Detect("super.conf", "client\nproto\u00B2 udp\nalpha one\nbeta two\ngamma three\n")!.Variant.Should().Be("generic-directive-text");
        Detect("combining.conf", "client\ndev\u0303 tun\nalpha one\nbeta two\ngamma three\n")!.Variant.Should().Be("openvpn-conf");
        Detect("sftp-digit.conf", "Port 22\nPermitRootLogin no\nSubsystem sftp\u2082 x\nUsePAM yes\n")!.Variant.Should().Be("generic-directive-text");
        Detect("sftp-mark.conf", "Port 22\nPermitRootLogin no\nSubsystem sftp\u0303 x\nUsePAM yes\n")!.Variant.Should().Be("openssh-conf");
    }

    // Python \w matches a supplementary-plane letter as one code point; a non-letter astral character ends the token.
    [Fact]
    public void AstralLettersContinueDirectiveTokens()
    {
        Detect("astral.conf", "a\U0001D41A one\nb\u1D41B two\nc\u1D41C three\nd\u1D41D four\n")!.Metadata!["directive_lines"].Should().Be(4);
        Detect("emoji.conf", "a\U0001F600 one\nb\U0001F600 two\nc\U0001F600 three\nd\U0001F600 four\n").Should().BeNull();
        Detect("emoji-boundary.conf", "client\nproto\U0001F600\nalpha one\nbeta two\ngamma three\n")!.Variant.Should().Be("openvpn-conf");
    }

    [Fact]
    public void DirectiveFirstCharacterIsAsciiButTailIsUnicode()
    {
        // Python: ^\s*[A-Za-z_][\w.-]* \u2014 a non-ASCII first letter is not a directive, a non-ASCII tail is.
        Detect("first.conf", "\u00FCber one\n\u00F1u two\nalpha three\nbeta four\ngamma five\n").Should().BeNull();
        Detect("tail.conf", "a\u00FC one\nb\u00F1 two\nc\u00E9 three\nd\u8A2D four\n")!.Metadata!["directive_lines"].Should().Be(4);
    }
}
