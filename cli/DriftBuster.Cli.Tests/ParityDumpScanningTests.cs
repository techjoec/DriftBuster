using System.CommandLine;

using DriftBuster.Cli;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// The diff, canon, hunt, secrets and secrets-context surfaces of <c>parity-dump</c>. Expected lines are
/// tools/parity/py_dump.py output for the same inputs (with the port's fix e hit added where it applies).
/// </summary>
public sealed class ParityDumpScanningTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-parity-scan-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_tmp.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var configuration = new InvocationConfiguration { Output = output, Error = error };
        var code = Program.BuildRootCommand().Parse(args).Invoke(configuration);
        return (code, output.ToString(), error.ToString());
    }

    private static string Invoke(params string[] args)
    {
        var (code, output, error) = Run(args);
        code.Should().Be(0, error);
        return output;
    }

    [Fact]
    public void Diff_prints_result_and_summary_with_masks_and_fixed_generated_at()
    {
        var before = Write("before.txt", "alpha\nbeta secret\n");
        var after = Write("after.txt", "alpha\ngamma secret\n");

        var line = Invoke("parity-dump", "diff", before, after, "--mask", "secret", "--placeholder", "#", "--context", "0").TrimEnd('\n');

        // key_order: py_dump.py _key_order of the same record (mapping insertion order, which the sorted keys hide).
        const string keyOrder = "[[\"result\", [[\"canonical_before\", null], [\"canonical_after\", null], [\"diff\", null], [\"stats\", [[\"added_lines\", null], [\"removed_lines\", null], [\"changed_lines\", null]]], [\"content_type\", null], [\"from_label\", null], [\"to_label\", null], [\"label\", null], [\"mask_tokens\", [null]], [\"placeholder\", null], [\"context_lines\", null], [\"redaction_counts\", [[\"secret\", null]]], [\"safety_limits\", null]]], [\"summary\", [[\"generated_at\", null], [\"versions\", [null, null]], [\"comparison_count\", null], [\"comparisons\", [[[\"from\", null], [\"to\", null], [\"plan\", [[\"content_type\", null], [\"from_label\", null], [\"to_label\", null], [\"label\", null], [\"mask_tokens\", [null]], [\"placeholder\", null], [\"context_lines\", null], [\"redaction_counts\", [[\"secret\", null]]], [\"binary_evidence\", []], [\"safety_limits\", null]]], [\"metadata\", [[\"content_type\", null], [\"context_lines\", null], [\"baseline_name\", null], [\"comparison_name\", null]]], [\"summary\", [[\"before_digest\", null], [\"after_digest\", null], [\"diff_digest\", null], [\"before_lines\", null], [\"after_lines\", null], [\"added_lines\", null], [\"removed_lines\", null], [\"changed_lines\", null]]]]]]]]]";
        line.Should().Be(
            "{\"key_order\": " + keyOrder + ", \"pair\": \"" + before + "\\t" + after + "\", \"result\": {\"canonical_after\": \"alpha\\ngamma secret\\n\", "
            + "\"canonical_before\": \"alpha\\nbeta secret\\n\", \"content_type\": \"text\", \"context_lines\": 0, "
            + "\"diff\": \"--- before.txt\\n+++ after.txt\\n@@ -2 +2 @@\\n-beta #\\n+gamma #\", \"from_label\": \"before.txt\", "
            + "\"label\": null, \"mask_tokens\": [\"secret\"], \"placeholder\": \"#\", \"redaction_counts\": {\"secret\": 2}, "
            + "\"safety_limits\": null, \"stats\": {\"added_lines\": 0, \"changed_lines\": 1, \"removed_lines\": 0}, \"to_label\": \"after.txt\"}, "
            + "\"summary\": {\"comparison_count\": 1, \"comparisons\": [{\"from\": \"before.txt\", \"metadata\": {\"baseline_name\": \"before.txt\", "
            + "\"comparison_name\": \"after.txt\", \"content_type\": \"text\", \"context_lines\": 0}, \"plan\": {\"binary_evidence\": [], "
            + "\"content_type\": \"text\", \"context_lines\": 0, \"from_label\": \"before.txt\", \"label\": null, \"mask_tokens\": [\"secret\"], "
            + "\"placeholder\": \"#\", \"redaction_counts\": {\"secret\": 2}, \"safety_limits\": null, \"to_label\": \"after.txt\"}, "
            + "\"summary\": {\"added_lines\": 0, \"after_digest\": \"sha256:95e787a1973a123f7fb4c1e9dc8ff93139e0fb244d8616631b511b38065fab2d\", "
            + "\"after_lines\": 2, \"before_digest\": \"sha256:c98981ace61c09374e0d89b2ba42eb2219985971acf1e73b50eaa120506e6cf1\", "
            + "\"before_lines\": 2, \"changed_lines\": 1, \"diff_digest\": \"sha256:6f2a446a8f2d00f80733e7f1137db4b3322a0422e99ebfb2bbb4eeefbdcb0d9f\", "
            + "\"removed_lines\": 0}, \"to\": \"after.txt\"}], \"generated_at\": \"<generated_at>\", \"versions\": [\"before.txt\", \"after.txt\"]}}");
    }

    // Python: result.redaction_counts in first-hit order, the summary payload's sorted by token. Tokens are tried longest first
    // on each line, so the inputs make the three orders differ: first hit value, secretvalue, secret; longest first secretvalue,
    // secret, value; sorted secret, secretvalue, value.
    [Fact]
    public void Diff_key_order_keeps_first_hit_redaction_order_and_the_sorted_summary_order()
    {
        var before = Write("b2.txt", "one value\n");
        var after = Write("a2.txt", "two secret secretvalue\n");

        var line = Invoke("parity-dump", "diff", before, after, "--mask", "value", "--mask", "secretvalue", "--mask", "secret").TrimEnd('\n');

        line.Should().Contain("[\"redaction_counts\", [[\"value\", null], [\"secretvalue\", null], [\"secret\", null]]], [\"safety_limits\", null]]]")
            .And.Contain("[\"redaction_counts\", [[\"secret\", null], [\"secretvalue\", null], [\"value\", null]]], [\"binary_evidence\", []]");
    }

    // Path(before) drops a trailing separator, so a pair spelled "x.xml/" diffs the file and labels it "x.xml".
    [Fact]
    public void Diff_spells_pair_paths_as_path_does()
    {
        var before = Write("before.xml", "<?xml version=\"1.0\"?>\n<r b=\"2\" a=\"1\"/>\n");
        var after = Write("after.xml", "<?xml version=\"1.0\"?>\n<r a=\"1\" b=\"3\"/>\n");

        var line = Invoke("parity-dump", "diff", before + "/", after + "//").TrimEnd('\n');

        line.Should().NotContain("\"error\"").And.Contain("\"content_type\": \"xml\"")
            .And.Contain("\"from_label\": \"before.xml\"").And.Contain("\"to_label\": \"after.xml\"");
    }

    // The parity surface reads bytes as UTF-8 with replacement (py_dump.py _read_replace), never through the GUI planner's
    // byte-order-mark detection: a UTF-16 file is two U+FFFD and NUL-interleaved text.
    [Fact]
    public void Diff_reads_a_utf16_file_as_utf8_with_replacement()
    {
        var before = Path.Combine(_tmp.FullName, "utf16.txt");
        File.WriteAllBytes(before, [0xFF, 0xFE, (byte)'a', 0x00]);
        var after = Write("plain.txt", "a\n");

        var line = Invoke("parity-dump", "diff", before, after, "--content-type", "text").TrimEnd('\n');

        line.Should().Contain("\"canonical_before\": \"\uFFFD\uFFFDa\\u0000\"");
    }

    [Fact]
    public void Diff_reads_pairs_resolves_content_type_and_reports_engine_errors()
    {
        var xmlBefore = Write("settings.txt", "<?xml version=\"1.0\"?>\n<r b=\"2\" a=\"1\"/>\n");
        var xmlAfter = Write("settings2.txt", "<?xml version=\"1.0\"?>\n<r a=\"1\" b=\"3\"/>\n");
        var labels = Write("labels.txt", "x\n");
        var pairs = Write("pairs.tsv", $"{xmlBefore}\t{xmlAfter}\n\n{labels}\t{Path.Combine(_tmp.FullName, "missing.txt")}\n");

        var lines = Invoke("parity-dump", "diff", "--pairs", pairs, "--label-from", "L", "--label-to", "R")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(2);
        lines[0].Should().Contain("\"canonical_before\": \"<?xml version=\\\"1.0\\\"?>\\n<r a=\\\"1\\\" b=\\\"2\\\" />\"").And.Contain("\"content_type\": \"xml\"")
            .And.Contain("\"from_label\": \"L\"").And.Contain("\"to_label\": \"R\"");
        lines[1].Should().StartWith("{\"error\": \"EngineError\", \"pair\": ");
    }

    [Fact]
    public void Diff_without_inputs_fails()
    {
        var (code, _, error) = Run("parity-dump", "diff", "--context", "1");

        code.Should().Be(2);
        error.Should().Contain("diff needs <before> <after> or --pairs");
    }

    [Fact]
    public void Canon_prints_the_canonical_text_per_file()
    {
        var target = Write("x.txt", "<r b=\"2\" a=\"1\"/>\n");

        Invoke("parity-dump", "canon", target, "--content-type", "xml").TrimEnd('\n')
            .Should().Be("{\"canonical\": \"<r a=\\\"1\\\" b=\\\"2\\\" />\", \"content_type\": \"xml\", \"path\": \"x.txt\"}");
        Invoke("parity-dump", "canon", Path.Combine(_tmp.FullName, "nope"), "--content-type", "text").TrimEnd('\n')
            .Should().Be("{\"content_type\": \"text\", \"error\": \"DetectorIOError\", \"path\": \".\"}");
    }

    [Fact]
    public void Hunt_replaces_the_root_prefix_and_applies_glob()
    {
        Write("h.cfg", "server host: app.corp.local\ninstall C:\\Program Files\\V path\n");
        Write("other.txt", "server host: other.corp.local\n");

        var lines = Invoke("parity-dump", "hunt", _tmp.FullName, "--glob", "*.cfg").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(2);
        lines[0].Should().Be(
            "{\"excerpt\": \"server host: app.corp.local\", \"line_number\": 1, \"metadata\": {\"plan_transform\": {\"placeholder\": \"{{ server_name }}\", "
            + "\"rule_name\": \"server-name\", \"token_name\": \"server_name\", \"value\": \"app.corp\"}}, \"path\": \"<root>/h.cfg\", "
            + "\"relative_path\": \"h.cfg\", \"rule\": {\"description\": \"Potential hostnames, server names, or FQDN references\", "
            + "\"keywords\": [\"server\", \"host\"], \"name\": \"server-name\", \"patterns\": [\"\\\\b[a-z0-9_-]+\\\\.(local|lan|corp|com|net|internal)\\\\b\"], "
            + "\"token_name\": \"server_name\"}}");
        lines[1].Should().Contain("\"name\": \"install-path\"").And.Contain("\"value\": \"C:\\\\Program Files\"");

        var fileRoot = Invoke("parity-dump", "hunt", Path.Combine(_tmp.FullName, "other.txt"), "--exclude", "*.cfg").TrimEnd('\n');
        fileRoot.Should().Contain("\"path\": \"<root>\"").And.Contain("\"relative_path\": \"other.txt\"");
    }

    [Fact]
    public void Hunt_reports_unreadable_files_as_a_record()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return; // mode 000 is not observable
        }

        var locked = Write("locked.txt", "server host: locked.corp.local\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Invoke("parity-dump", "hunt", _tmp.FullName).TrimEnd('\n').Should().Be("{\"unreadable_files\": [\"" + locked + "\"]}");
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // py_dump.py _walk lists an entry the detector reported without scanning it (is_file() raising EACCES inside a directory
    // that cannot be searched) as a DetectorIOError record, in walk order.
    [Fact]
    public void Secrets_lists_a_file_whose_stat_is_refused_as_an_error_record()
    {
        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess)
        {
            return; // search permission is observable only for a non-root Linux user
        }

        Write("a.conf", "alpha one\nbeta two\n");
        Write("z.conf", "alpha one\nbeta two\n");
        var sub = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "rdir"));
        File.WriteAllText(Path.Combine(sub.FullName, "x.conf"), "alpha one\n");
        File.SetUnixFileMode(sub.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            var lines = Invoke("parity-dump", "secrets", _tmp.FullName).Split('\n', StringSplitOptions.RemoveEmptyEntries);

            lines.Should().HaveCount(3);
            lines[1].Should().Be("{\"error\": \"DetectorIOError\", \"path\": \"rdir/x.conf\"}");
            lines[0].Should().Contain("\"path\": \"a.conf\"");
            lines[2].Should().Contain("\"path\": \"z.conf\"");
        }
        finally
        {
            File.SetUnixFileMode(sub.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Secrets_prints_the_filtered_copy()
    {
        var target = Write("s.txt", "password=Sup3rSecretValue\nok\n");

        Invoke("parity-dump", "secrets", target).TrimEnd('\n').Should().Be(
            "{\"findings\": [{\"line\": 1, \"rule\": \"PasswordAssignment\", \"snippet\": \"[SECRET]\"}], "
            + "\"log\": [\"secret candidate redacted (PasswordAssignment) from s.txt:1 -> [SECRET]\", \"scrubbed 1 potential secret line(s) from s.txt\"], "
            + "\"output_text\": \"[SECRET]\\nok\\n\", \"path\": \"s.txt\", "
            + "\"sha256\": \"bdfecbab8e020fba66e053e5d10ec4b6ce537cc586e602569ae70a370e3c7fb2\", \"size\": 12}");
        Invoke("parity-dump", "secrets", Path.Combine(_tmp.FullName, "nope")).TrimEnd('\n')
            .Should().Be("{\"error\": \"DetectorIOError\", \"path\": \".\"}");
    }

    // A FIFO the walk opened would block this test forever; Path.is_file() is False for it, the socket and the device link.
    [Fact(Timeout = 30_000)]
    public async Task Secrets_skips_fifos_sockets_and_devices_and_spells_a_file_root_as_path_does()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "FIFOs and Unix sockets in a walked tree are a Linux case");
        var tree = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "tree")).FullName;
        var fifo = Path.Combine(tree, "pipe.env");
        var start = new System.Diagnostics.ProcessStartInfo("mkfifo") { ArgumentList = { fifo } };
        using (var mkfifo = System.Diagnostics.Process.Start(start)!)
        {
            await mkfifo.WaitForExitAsync(TestContext.Current.CancellationToken);
            mkfifo.ExitCode.Should().Be(0);
        }

        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(Path.Combine(tree, "socket.env")));
        File.CreateSymbolicLink(Path.Combine(tree, "device.env"), "/dev/null");
        var plain = Path.Combine(tree, "plain.env");
        File.WriteAllText(plain, "ok\n");

        var lines = await Task.Run(() => Invoke("parity-dump", "secrets", tree), TestContext.Current.CancellationToken);
        var fileRoot = await Task.Run(() => Invoke("parity-dump", "secrets", plain + "/"), TestContext.Current.CancellationToken);

        lines.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle().Which.Should().Contain("\"path\": \"plain.env\"");
        fileRoot.TrimEnd('\n').Should().Be(lines.TrimEnd('\n'));
    }

    // Fix g: Python never returns on loop.txt; the port record carries the guard it applied.
    [Fact]
    public void Secrets_with_a_ruleset_reports_the_redaction_guard()
    {
        var ruleset = Write("ruleset.json", "{\"version\": \"loop\", \"rules\": [{\"name\": \"SelfMatching\", \"pattern\": \"secret\", \"flags\": \"i\"}]}");
        var input = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "input")).FullName;
        File.WriteAllText(Path.Combine(input, "loop.txt"), "token secret\n");
        File.WriteAllText(Path.Combine(input, "none.txt"), "nothing\n");

        var lines = Invoke("parity-dump", "secrets", input, "--ruleset", ruleset).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(2);
        lines[0].Should().Contain("\"output_text\": \"token [SECRET]\\n\"").And.Contain("\"redaction_guard\": [{\"line\": 1, \"rule\": \"SelfMatching\"}]");
        lines[1].Should().Contain("\"path\": \"none.txt\"").And.NotContain("redaction_guard");
    }

    [Fact]
    public void Secrets_context_prints_context_and_manifest()
    {
        Write("ctx.json", "{\"options\": {\"secret_ignore_rules\": \"A,B\"}, \"secret_scanner\": {\"ignore_patterns\": [\"x\", \"(\"]}}");
        Write("no-manifest.json", "{\"options\": null}");
        Write("ignored.txt", "not json");

        var lines = Invoke("parity-dump", "secrets-context", _tmp.FullName).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(2);
        lines[0].Should().Be(
            "{\"context\": {\"ignore_pattern_text\": [\"x\", \"(\"], \"ignore_patterns\": [\"x\"], \"ignore_rules\": [\"A\", \"B\"], \"rules\": ["
            + "{\"description\": \"Matches common password assignment patterns in configuration files.\", \"flags\": 34, \"name\": \"PasswordAssignment\", "
            + "\"pattern\": \"(?i)password\\\\s*[:=]\\\\s*['\\\"]?[A-Za-z0-9\\\\-_/+=]{8,}\"}, "
            + "{\"description\": \"Detects API key or token style strings with obvious labels.\", \"flags\": 34, \"name\": \"GenericApiToken\", "
            + "\"pattern\": \"(?i)(api|auth|token)[-_ ]?(key|token)\\\\s*[:=]\\\\s*['\\\"]?[A-Za-z0-9]{16,}\"}, "
            + "{\"description\": \"AWS-style access key identifiers.\", \"flags\": 32, \"name\": \"AwsAccessKeyId\", \"pattern\": \"AKIA[0-9A-Z]{16}\"}], "
            + "\"rules_loaded\": true, \"version\": \"2024-06-01\"}, \"manifest\": {\"ignore_patterns\": [\"(\", \"x\"], \"ignore_rules\": [\"A\", \"B\"], "
            + "\"ruleset_version\": \"2024-06-01\"}, \"path\": \"ctx.json\"}");
        lines[1].Should().StartWith("{\"context\": {").And.EndWith("\"path\": \"no-manifest.json\"}").And.NotContain("manifest\":");

        var bad = Write("bad.json", "[1]");
        var act = () => ParityDump.SecretsContext(bad).ToList();
        act.Should().Throw<InvalidDataException>();
    }
}
