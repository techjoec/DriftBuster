using System.Text;
using System.Text.Json;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Backend.Tests.MultiServer;

/// <summary>
/// Operator decisions S and R (expected_divergences.md, "Python raises on unpaired surrogates (not reproduced)"): where Python
/// fails a host or aborts the run only because a str holding an unpaired surrogate reaches a strict UTF-8 encode, the port keeps
/// working; a root holding an unpaired surrogate is never opened under a guessed spelling and counts as not found, and no path
/// through a link whose target is not UTF-8 is opened under the U+FFFD spelling the runtime gives that target.
/// </summary>
public sealed class MultiServerSurrogateTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-multi-server-surrogates-");

    public void Dispose() => SpecialFiles.DeleteTree(_tmp);

    private string CacheDir => Path.Combine(_tmp.FullName, "cache");

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_tmp.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Dir(string relative) => Path.Combine(_tmp.FullName, relative);

    private static MultiServerPlan Plan(string host, params string[] roots) => new() { HostId = host, Label = host, Roots = roots };

    // Trigger 1: json.loads keeps "\ud800key" in top_level_keys; DiffCache.save's write_text raises UnicodeEncodeError after
    // truncating the entry and the host goes offline in Python. The port writes the entry (U+FFFD for the surrogate) and reuses it.
    [Fact]
    public void ALoneSurrogateInDetectionMetadataIsCachedAndTheHostSucceeds()
    {
        Write("hostA/app.json", "{\"\\ud800key\": \"v\", \"Server\": \"a\"}\n");
        Write("hostB/app.json", "{\"key\": \"v\", \"Server\": \"b\"}\n");
        var plans = new[] { Plan("hostA", Dir("hostA")) with { IsPreferred = true }, Plan("hostB", Dir("hostB")) };

        var first = new MultiServerRunner(CacheDir).Run(plans, cancellationToken: TestContext.Current.CancellationToken);
        var second = new MultiServerRunner(CacheDir).Run(plans, cancellationToken: TestContext.Current.CancellationToken);

        first.Results.Select(result => (result.Status, result.Availability, result.Message)).Should().AllBeEquivalentTo(
            (ServerScanStatus.Succeeded, ServerAvailabilityStatus.Found, "Evaluated 1 configuration(s)."));
        first.Catalog.Should().ContainSingle().Which.PresentHosts.Should().Equal("hostA", "hostB");
        var entries = Directory.GetFiles(CacheDir).Select(File.ReadAllBytes).ToList();
        entries.Should().HaveCount(2);
        entries.Select(bytes => new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes))
            .Should().ContainSingle(text => text.Contains("\uFFFDkey", StringComparison.Ordinal));
        second.Results.Should().AllSatisfy(result => result.UsedCache.Should().BeTrue());
    }

    // Trigger 2: sha256(f"{host_id}:{config_id}:...".encode()) raises for a host id holding a lone surrogate once its scan builds a
    // record, so that host goes offline in Python. The port hashes the id (U+FFFD for the surrogate) and scans the host.
    [Fact]
    public void ALoneSurrogateInAHostIdScansTheHost()
    {
        Write("hostA/app.ini", "[core]\nname = a\n");
        Write("hostB/app.ini", "[core]\nname = b\n");
        var plans = new[] { Plan("hostA", Dir("hostA")) with { IsPreferred = true }, Plan("host\ud800B", Dir("hostB")) };

        var first = new MultiServerRunner(CacheDir).Run(plans, cancellationToken: TestContext.Current.CancellationToken);
        var second = new MultiServerRunner(CacheDir).Run(plans, cancellationToken: TestContext.Current.CancellationToken);

        first.Results[1].Status.Should().Be(ServerScanStatus.Succeeded);
        first.Results[1].Message.Should().Be("Evaluated 1 configuration(s).");
        var row = first.Drilldown.Should().ContainSingle().Which.Servers.Single(server => string.Equals(server.HostId, "host\ud800B", StringComparison.Ordinal));
        row.Present.Should().BeTrue();
        row.DriftLineCount.Should().BePositive();
        first.Catalog[0].DriftCount.Should().Be(1);
        second.Results[1].UsedCache.Should().BeTrue();
    }

    // Trigger 3: a label holding a lone surrogate heads a non-empty unified diff; build_unified_diff's clamp encodes the diff text
    // strictly and run() raises in Python. The port builds the catalog with the label kept in the diff headers.
    [Fact]
    public void ALoneSurrogateInAPlanLabelHeadingADiffCompletesTheRun()
    {
        Write("hostA/app.ini", "[core]\nname = a\n");
        Write("hostB/app.ini", "[core]\nname = b\n");
        var plans = new[] { Plan("hostA", Dir("hostA")) with { IsPreferred = true }, Plan("hostB", Dir("hostB")) with { Label = "host\udfffB" } };

        var response = new MultiServerRunner(CacheDir).Run(plans, cancellationToken: TestContext.Current.CancellationToken);

        response.Results.Should().AllSatisfy(result => result.Status.Should().Be(ServerScanStatus.Succeeded));
        response.Catalog.Should().ContainSingle().Which.PresentHosts.Should().Equal("hostA", "host\udfffB");
        response.Catalog[0].DriftCount.Should().Be(1);
        response.Drilldown[0].DiffSummary.Should().NotBeNull();
    }

    // A label heading only empty diffs raises nothing in Python, whose diff_summary keeps the surrogate; the port model stores the
    // summary as a JsonElement, which is UTF-8 and holds U+FFFD (expected_divergences.md, "Port-model limit: an unpaired surrogate
    // in diff_summary"; parity case close-r2/surrogate-label-empty-diffs).
    [Fact]
    public void ALoneSurrogateInALabelReachesTheDiffSummaryAsAReplacementCharacter()
    {
        Write("hostA/app.ini", "[core]\nname = same\n");
        Write("hostB/app.ini", "[core]\nname = same\n");
        var plans = new[] { Plan("hostA", Dir("hostA")) with { IsPreferred = true, Label = "host\ud800A" }, Plan("hostB", Dir("hostB")) };

        var response = new MultiServerRunner(CacheDir).Run(plans, cancellationToken: TestContext.Current.CancellationToken);

        response.Catalog.Should().ContainSingle().Which.DriftCount.Should().Be(0);
        var versions = response.Drilldown[0].DiffSummary!.Value.GetProperty("versions").EnumerateArray().Select(item => item.GetString()).ToList();
        versions.Should().AllSatisfy(version => version.Should().StartWith("host\uFFFDA:"));
    }

    // Decision R, '\ud800': Path.exists() cannot encode it and returns False, so the only root is not found. The port must not look
    // up the U+FFFD spelling UTF-8 replacement gives, which here names a real directory holding a config.
    [Fact]
    public void AnOnlyRootHoldingAnUnpairedSurrogateIsNotFound()
    {
        Write("trap/\uFFFD/app.ini", "[core]\nname = trap\n");
        var root = Dir("trap/\ud800");

        var response = new MultiServerRunner(CacheDir).Run([Plan("hostA", root)], cancellationToken: TestContext.Current.CancellationToken);

        var result = response.Results.Should().ContainSingle().Subject;
        (result.Status, result.Availability, result.Message).Should().Be((ServerScanStatus.Failed, ServerAvailabilityStatus.NotFound, "No accessible roots."));
        result.Roots.Should().Equal(root);
        response.Catalog.Should().BeEmpty();
    }

    // Decision R, '\udcff' next to a root that exists: the surrogate root is absent from existing_roots, whatever the U+FFFD
    // spelling or the byte-0xFF name holds. Python's surrogateescape stat finds the byte-0xFF directory, scans it and goes offline
    // on the root fingerprint encode; the port scans only the other root.
    [Fact]
    public void ARootHoldingAnUnpairedSurrogateIsLeftOutOfTheExistingRoots()
    {
        Write("trap/d\uFFFD/app.ini", "[core]\nname = trap\n");
        if (OperatingSystem.IsLinux())
        {
            SpecialFiles.CreateUndecodableDirectory(Dir("trap"), "[core]\nname = bytes\n");
        }

        Write("good/app.ini", "[core]\nname = good\n");
        var surrogateRoot = Dir("trap/d\udcff");

        var response = new MultiServerRunner(CacheDir).Run(
            [Plan("hostA", surrogateRoot, Dir("good"))],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = response.Results.Should().ContainSingle().Subject;
        (result.Status, result.Message).Should().Be((ServerScanStatus.Succeeded, "Evaluated 1 configuration(s)."));
        result.Roots.Should().Equal(Dir("good"));
        response.Drilldown.Should().ContainSingle().Which.DiffAfter.Should().Contain("good");
    }

    // Trigger 5: os.path.expanduser('~\ud800/x') encodes the user name for pwd.getpwnam, and os.path.expandvars('${\ud800}/x') and
    // '${HO\ud800ME}' encode the variable name for os.environ; each raises UnicodeEncodeError out of _build_plans, so Python answers
    // no plan at all. The port keeps each such root unexpanded, leaves it out as decision R says, and scans the rest.
    [Fact]
    public void ARootWhoseExpansionEncodesAnUnpairedSurrogateIsLeftOutAndTheRestIsScanned()
    {
        Write("hostA/app.ini", "[core]\nname = a\n");
        Write("hostB/app.ini", "[core]\nname = b\n");
        var request = $$$"""
            {"plans": [
             {"host_id": "hostA", "roots": [{{{Quote(Dir("hostA"))}}}], "baseline": {"is_preferred": true}},
             {"host_id": "hostB", "roots": ["~\ud800/x", "${\ud800}/x", {{{Quote(Dir("hostB"))}}}]},
             {"host_id": "hostC", "roots": ["${HO\ud800ME}"]}
            ]}
            """;
        PythonJson.TryLoads(request, out var decoded).Should().BeTrue();

        var plans = MultiServerPlan.BuildPlans(decoded);
        var response = new MultiServerRunner(CacheDir).Run(plans, cancellationToken: TestContext.Current.CancellationToken);

        plans[1].Roots.Should().Equal("~\ud800/x", "${\ud800}/x", Dir("hostB"));
        plans[2].Roots.Should().Equal("${HO\ud800ME}");
        response.Results.Select(result => (result.Status, result.Availability, result.Message)).Should().Equal(
            (ServerScanStatus.Succeeded, ServerAvailabilityStatus.Found, "Evaluated 1 configuration(s)."),
            (ServerScanStatus.Succeeded, ServerAvailabilityStatus.Found, "Evaluated 1 configuration(s)."),
            (ServerScanStatus.Failed, ServerAvailabilityStatus.NotFound, "No accessible roots."));
        response.Results[1].Roots.Should().Equal(Dir("hostB"));
        response.Results[2].Roots.Should().Equal("${HO\ud800ME}");
        response.Catalog.Should().ContainSingle().Which.DriftCount.Should().Be(1);
    }

    private static string Quote(string text) => JsonSerializer.Serialize(text);

    // Trigger 4: a root that is a symlink to a directory whose name is not UTF-8, or a root below such a link. The root string holds
    // no surrogate, but Python's root.resolve() in the root fingerprint yields '\udcff' and the sha1 encode takes the host offline.
    // The kernel follows the link for the port, which scans the directory and hashes the physical path with U+FFFD.
    [Fact]
    public void ARootThroughALinkToADirectoryWithoutAUtf8NameIsScanned()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "names and link targets are bytes only on Linux here");
        Write("base/app.ini", "[core]\nname = base\n");
        SpecialFiles.Shell(
            "mkdir -p \"$1/undecodable/$ff/sub\" \"$1/links\" && printf '[core]\\nname = bytes\\n' > \"$1/undecodable/$ff/app.ini\""
            + " && printf '[core]\\nname = sub\\n' > \"$1/undecodable/$ff/sub/sub.ini\" && ln -s \"$1/undecodable/$ff\" \"$1/links/link\"",
            _tmp.FullName);
        var plans = new[]
        {
            Plan("base", Dir("base")) with { IsPreferred = true },
            Plan("hostB", Dir("links/link")),
            Plan("hostC", Dir("links/link/sub")),
        };

        var first = new MultiServerRunner(CacheDir).Run(plans, cancellationToken: TestContext.Current.CancellationToken);
        var second = new MultiServerRunner(CacheDir).Run(plans, cancellationToken: TestContext.Current.CancellationToken);

        first.Results.Select(result => (result.HostId, result.Status, result.Message)).Should().Equal(
            ("base", ServerScanStatus.Succeeded, "Evaluated 1 configuration(s)."),
            ("hostB", ServerScanStatus.Succeeded, "Evaluated 2 configuration(s)."),
            ("hostC", ServerScanStatus.Succeeded, "Evaluated 1 configuration(s)."));
        second.Results.Skip(1).Should().AllSatisfy(result => result.UsedCache.Should().BeTrue());
        MultiServerRunner.RootFingerprint([Dir("links/link")]).Should().Be(
            MultiServerPlan.Sha1Hex($"{PythonPathOf(Dir("undecodable"))}/\uFFFD"), "the physical path is hashed with U+FFFD for the byte 0xFF");
    }

    // Decision R's rule for a '..' after a link: links/trap -> trap/<0xFF>, and trap/U+FFFD -> elsewhere/deep. The kernel (and Python)
    // reach trap/x through links/trap/../x; the runtime's U+FFFD reading of the link target leads to elsewhere/x instead. The port
    // walks and reads trap/x, never the guessed name.
    [Fact]
    public void ARootWithDotDotAfterALinkToANameThatIsNotUtf8ReadsTheDirectoryTheKernelReaches()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "names and link targets are bytes only on Linux here");
        Write("base/app.ini", "[core]\nname = base\n");
        SpecialFiles.Shell(
            "mkdir -p \"$1/trap/$ff\" \"$1/trap/x\" \"$1/elsewhere/deep\" \"$1/elsewhere/x\" \"$1/links\""
            + " && printf '[core]\\nname = real\\n' > \"$1/trap/x/app.ini\" && printf '[core]\\nname = guessed\\n' > \"$1/elsewhere/x/app.ini\""
            + " && ln -s \"$1/elsewhere/deep\" \"$1/trap/$fffd\" && ln -s \"$1/trap/$ff\" \"$1/links/trap\"",
            _tmp.FullName);

        var response = new MultiServerRunner(CacheDir).Run(
            [Plan("hostB", $"{_tmp.FullName}/links/trap/../x") with { IsPreferred = true }, Plan("base", Dir("base"))],
            cancellationToken: TestContext.Current.CancellationToken);

        response.Results.Should().AllSatisfy(result => result.Status.Should().Be(ServerScanStatus.Succeeded));
        response.Drilldown.Should().ContainSingle().Which.DiffBefore.Should().Be("[core]\nname = real\n");
    }

    // Platform limit: a root whose '..' leaves the kernel inside a directory that no UTF-8 string names (dotdot/deeper -> <0xFF>/sub,
    // root dotdot/deeper/..). Python scans it and then goes offline on the fingerprint encode; the port cannot name the directory,
    // opens nothing under a guessed name, and fails the host as a root it cannot read.
    [Fact]
    public void ARootWhoseDotDotEndsInADirectoryWithoutAUtf8NameIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "names and link targets are bytes only on Linux here");
        SpecialFiles.Shell(
            "mkdir -p \"$1/dotdot/$ff/sub\" \"$1/dotdot/$fffd\" && printf '[core]\\nname = bytes\\n' > \"$1/dotdot/$ff/app.ini\""
            + " && printf '[core]\\nname = guessed\\n' > \"$1/dotdot/$fffd/app.ini\" && ln -s \"$ff/sub\" \"$1/dotdot/deeper\"",
            _tmp.FullName);
        var root = $"{_tmp.FullName}/dotdot/deeper/..";

        var response = new MultiServerRunner(CacheDir).Run([Plan("hostA", root)], cancellationToken: TestContext.Current.CancellationToken);

        var result = response.Results.Should().ContainSingle().Subject;
        (result.Status, result.Availability, result.Message).Should().Be(
            (ServerScanStatus.Failed, ServerAvailabilityStatus.PermissionDenied, $"Permission denied: {root}"));
        response.Catalog.Should().BeEmpty();
        Directory.GetFiles(CacheDir).Should().BeEmpty();
    }

    // DiffCache.save's write_text follows a symlink entry to a target whose name is not UTF-8 and writes it. The port cannot name that
    // target, so it writes the entry in place through the link instead of beside the U+FFFD spelling (which here is a real directory).
    [Fact]
    public void ACacheEntryLinkedToANameThatIsNotUtf8IsWrittenThroughTheLink()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "names and link targets are bytes only on Linux here");
        var cache = new DiffCache(CacheDir);
        var entry = cache.EntryPath("hostA", "config");
        SpecialFiles.Shell(
            "mkdir -p \"$1/store/$fffd\" && : > \"$1/store/$ff\" && ln -s \"$1/store/$ff\" \"$2\"",
            _tmp.FullName, entry);
        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["canonical"] = "x" };

        cache.Save("hostA", "config", "sig", payload, TestContext.Current.CancellationToken);

        cache.Load("hostA", "config", "sig").Should().NotBeNull();
        Directory.EnumerateFileSystemEntries(Dir("store/\uFFFD")).Should().BeEmpty("nothing is written beside the guessed name");
        Directory.GetFiles(Dir("store")).Should().ContainSingle();
        new FileInfo(entry).LinkTarget.Should().NotBeNull("the entry is still the link");
    }

    // _resolve_cache_dir(cache_dir) resolves an explicit directory; through a link to a name that is not UTF-8, Python's resolve()
    // gives the surrogateescape name, which its file calls accept. The port cannot name the physical directory, so it keeps the
    // spelling through the link, which reaches the same directory, and never creates the U+FFFD spelling.
    [Fact]
    public void ACacheDirectoryThroughALinkToANameThatIsNotUtf8IsUsedThroughTheLink()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "names and link targets are bytes only on Linux here");
        SpecialFiles.Shell("mkdir -p \"$1/store/$ff\" \"$1/links\" && ln -s \"$1/store/$ff\" \"$1/links/cache\"", _tmp.FullName);

        var resolved = DiffCache.ResolveCacheDirectory(Dir("links/cache"), repositoryRoot: null);
        var cache = new DiffCache(resolved);
        cache.Save("hostA", "config", "sig", new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["canonical"] = "x" }, TestContext.Current.CancellationToken);

        resolved.Should().Be(Dir("links/cache"));
        Directory.Exists(Dir("store/\uFFFD")).Should().BeFalse("the U+FFFD spelling of the physical directory is never created");
        cache.Load("hostA", "config", "sig").Should().NotBeNull();
        Directory.GetFiles(Dir("links/cache")).Should().ContainSingle();
    }

    private static string PythonPathOf(string path) => PythonPath.ResolvePhysicalPath(path)!;
}
