using System.CommandLine;
using System.Text;

using DriftBuster.Cli;

namespace DriftBuster.Cli.Tests;

public sealed class ParityDumpTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-parity-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Write(string relative, byte[] content)
    {
        var path = Path.Combine([_tmp.FullName, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Invoke(params string[] args)
    {
        var output = new StringWriter();
        var configuration = new InvocationConfiguration { Output = output, Error = new StringWriter() };
        var code = Program.BuildRootCommand().Parse(args).Invoke(configuration);
        code.Should().Be(0, output.ToString());
        return output.ToString();
    }

    [Fact]
    public void Parity_dump_is_hidden_from_help()
    {
        var output = new StringWriter();
        var configuration = new InvocationConfiguration { Output = output, Error = new StringWriter() };
        Program.BuildRootCommand().Parse(["--help"]).Invoke(configuration).Should().Be(0);
        output.ToString().Should().NotContain("parity-dump");
    }

    // Records come in Detector.ScanPath order: sorted(Path) compares components, so "a/plain.txt" precedes "a.bin".
    [Fact]
    public void Detect_prints_python_shaped_lines_in_scan_path_order()
    {
        Write("b/sshd_config", "Port 22\nPermitRootLogin no\nSubsystem sftp internal-sftp\nUsePAM yes\n"u8.ToArray());
        Write("a.bin", [0x00, 0xFF, 0x10, 0x80]);
        Write("a/plain.txt", "just one line"u8.ToArray());

        var lines = Invoke("parity-dump", "detect", _tmp.FullName, "--plugins", "text").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(3);
        lines[0].Should().StartWith("{\"confidence\": null, \"format\": null, \"metadata\": null, \"path\": \"a/plain.txt\"");
        lines[1].Should().Be(
            "{\"confidence\": null, \"format\": null, \"metadata\": null, \"path\": \"a.bin\", \"plugin\": null, \"reasons\": [], \"variant\": null}");
        lines[2].Should().StartWith("{\"confidence\": 0.8, \"format\": \"unix-conf\", \"metadata\": {\"bytes_sampled\": 67, \"catalog_format\": \"unix-conf\"");
        lines[2].Should().Contain("\"path\": \"b/sshd_config\", \"plugin\": \"text\", \"reasons\": [\"Detected Whitespace-Delimited Directives With Minimal Assignments\"");
        lines[2].Should().EndWith("\"variant\": \"openssh-conf\"}");
    }

    [Fact]
    public void Detect_on_a_single_file_uses_its_name_and_reports_truncation()
    {
        var target = Write("client.conf", Encoding.UTF8.GetBytes("client\ndev tun\nproto udp\nremote host 1194\nnobind\npersist-key\n"));

        var line = Invoke("parity-dump", "detect", target, "--plugins", "text", "--sample-size", "32").TrimEnd('\n');

        line.Should().Contain("\"path\": \"client.conf\"");
        line.Should().Contain("\"sample_truncated\": true");
        line.Should().Contain("\"Truncated Sample To 32B\"");
    }

    [Fact]
    public void Detect_stops_after_the_file_that_exhausts_the_budget()
    {
        var directives = "alpha one\nbeta two\ngamma three\ndelta four\n"u8.ToArray();
        Write("a.conf", directives);
        Write("b.conf", directives);
        Write("c.conf", directives);

        var lines = Invoke("parity-dump", "detect", _tmp.FullName, "--plugins", "text", "--max-total-sample-bytes", "80")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(2);
        lines[0].Should().Contain("\"path\": \"a.conf\"").And.NotContain("sample_budget_exhausted");
        lines[1].Should().Contain("\"path\": \"b.conf\"").And.Contain("\"sample_budget_exhausted\": true")
            .And.Contain("\"Sampling Budget Exhausted After 38B\"");
    }

    [Fact]
    public void Missing_and_dangling_roots_emit_a_root_error_record()
    {
        Invoke("parity-dump", "detect", Path.Combine(_tmp.FullName, "nope")).TrimEnd('\n')
            .Should().Be("{\"error\": \"DetectorIOError\", \"path\": \".\"}");
        Invoke("parity-dump", "decode", Path.Combine(_tmp.FullName, "nope")).TrimEnd('\n')
            .Should().Be("{\"error\": \"DetectorIOError\", \"path\": \".\"}");

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dangling = Path.Combine(_tmp.FullName, "dangling");
        File.CreateSymbolicLink(dangling, Path.Combine(_tmp.FullName, "gone"));
        Invoke("parity-dump", "detect", dangling).TrimEnd('\n').Should().Be("{\"error\": \"DetectorIOError\", \"path\": \".\"}");
    }

    [Fact]
    public void Walk_skips_dangling_links_and_records_unreadable_entries()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return; // mode 000 is not observable
        }

        var directives = "alpha one\nbeta two\ngamma three\ndelta four\n"u8.ToArray();
        var readable = Write("tree/readable.conf", directives);
        var unreadable = Write("tree/unreadable.conf", directives);
        Write("tree/locked/hidden.conf", directives);
        var tree = Path.Combine(_tmp.FullName, "tree");
        File.CreateSymbolicLink(Path.Combine(tree, "dangling"), Path.Combine(tree, "nowhere"));
        File.CreateSymbolicLink(Path.Combine(tree, "linkfile"), readable);
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        File.SetUnixFileMode(Path.Combine(tree, "locked"), UnixFileMode.None);
        try
        {
            var walk = ParityDump.Walk(tree);
            walk.Select(entry => (entry.Relative, entry.Errored)).Should().Equal(("linkfile", false), ("readable.conf", false), ("unreadable.conf", true));

            var lines = Invoke("parity-dump", "detect", tree, "--plugins", "text").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Should().HaveCount(3);
            lines[2].Should().Be("{\"error\": \"DetectorIOError\", \"path\": \"unreadable.conf\"}");
            Invoke("parity-dump", "decode", tree).Split('\n', StringSplitOptions.RemoveEmptyEntries)[2]
                .Should().Be("{\"error\": \"DetectorIOError\", \"path\": \"unreadable.conf\"}");
        }
        finally
        {
            File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(Path.Combine(tree, "locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Decode_reports_codec_and_hash()
    {
        Write("bom.txt", [0xEF, 0xBB, 0xBF, (byte)'a']);
        Write("bin.dat", [0xFF, 0xFE, 0x80]);

        var lines = Invoke("parity-dump", "decode", _tmp.FullName).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().Equal(
            "{\"codec\": \"latin-1\", \"looks_text\": true, \"path\": \"bin.dat\", \"text_sha256\": \"" + Sha256Hex("\u00ff\u00fe\u0080") + "\"}",
            "{\"codec\": \"utf-8-sig\", \"looks_text\": true, \"path\": \"bom.txt\", \"text_sha256\": \"" + Sha256Hex("a") + "\"}");
    }

    [Fact]
    public void Select_plugins_filters_by_name()
    {
        ParityDump.SelectPlugins(null).Should().BeNull();
        ParityDump.SelectPlugins("text, missing").Should().ContainSingle().Which.Name.Should().Be("text");
        ParityDump.SelectPlugins("nothing").Should().BeEmpty();
    }

    private static string Sha256Hex(string text)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
