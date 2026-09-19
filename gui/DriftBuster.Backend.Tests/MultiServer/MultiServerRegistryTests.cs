using System.Text;
using System.Text.Json.Nodes;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;
using DriftBuster.Backend.Registry;
using DriftBuster.Backend.Tests.Secrets;

namespace DriftBuster.Backend.Tests.MultiServer;

/// <summary>Registry keys in a multi-server run: read through a reader, rendered as .reg records, compared like files.</summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class MultiServerRegistryTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-multi-server-registry-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private static RegistryRawValue Sz(string name, string value) => new(name, RegistryValueDecoder.RegSz, Encoding.Unicode.GetBytes(value + "\0"));

    private static RegistryRawValue Dword(string name, uint value) => new(name, RegistryValueDecoder.RegDword, BitConverter.GetBytes(value));

    /// <summary>A reader over fixed nodes that records what it was asked for.</summary>
    private sealed class FakeReader(params RegistryTreeNode[] nodes) : IRegistryTreeReader
    {
        public List<(IReadOnlyList<RegistryRoot> Roots, int Depth)> Reads { get; } = [];

        public RegistryTreeRead Read(IReadOnlyList<RegistryRoot> roots, int maxDepth, CancellationToken cancellationToken)
        {
            Reads.Add((roots, maxDepth));
            return new RegistryTreeRead(nodes.Where(node => roots.Any(root => node.Path.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(node.Hive, root.Hive, StringComparison.Ordinal))).ToList(), Truncated: false);
        }
    }

    private static MultiServerPlan Plan(string host, MultiServerRegistry registry, params string[] roots) =>
        new() { HostId = host, Label = host, Roots = roots, Registry = registry };

    [Fact]
    public void RegistryKeysAreComparedAcrossHostsLikeFiles()
    {
        var readers = new Dictionary<string, FakeReader>(StringComparer.Ordinal)
        {
            ["app-01"] = new(
                new RegistryTreeNode("HKLM", @"SOFTWARE\Vendor", null, ["Suite"], [Sz("", "root"), Sz("Server", "api.one")]),
                new RegistryTreeNode("HKLM", @"SOFTWARE\Vendor\Suite", null, [], [Dword("Port", 443)])),
            ["app-02"] = new(
                new RegistryTreeNode("HKLM", @"SOFTWARE\Vendor", null, ["Suite"], [Sz("", "root"), Sz("Server", "api.two")]),
                new RegistryTreeNode("HKLM", @"SOFTWARE\Vendor\Suite", null, [], [Dword("Port", 443)])),
        };
        var runner = new MultiServerRunner(Path.Combine(_tmp.FullName, "cache"))
        {
            RegistryReaderFactory = registry => readers[registry.Computer!],
        };

        var response = runner.Run(
            [
                Plan("a", new MultiServerRegistry([@"HKLM\SOFTWARE\Vendor", @"HKLM\SOFTWARE\Vendor\Suite"], "app-01", null)),
                Plan("b", new MultiServerRegistry([@"HKLM\SOFTWARE\Vendor"], "app-02", null)),
            ],
            cancellationToken: TestContext.Current.CancellationToken);

        response.Results.Select(result => result.Status).Should().AllBeEquivalentTo(ServerScanStatus.Succeeded);
        response.Results[0].Message.Should().Be("Evaluated 1 configuration(s). Read 1 registry key(s) on app-01.");
        var file = response.Comparison.Files.Should().ContainSingle().Subject;
        file.Path.Should().Be("registry/HKLM/SOFTWARE/Vendor.reg");
        file.Format.Should().Be("registry-export");
        var rows = file.Settings.ToDictionary(row => row.Key, StringComparer.Ordinal);
        rows[@"[HKEY_LOCAL_MACHINE\SOFTWARE\Vendor] Server"].Differs.Should().BeTrue();
        rows[@"[HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\Suite] Port"].Values.Select(value => value.Value).Should().AllBe("dword:000001bb (443)");
        readers["app-01"].Reads.Should().ContainSingle().Which.Roots.Should().ContainSingle("the nested key is part of its parent's record");
    }

    [Fact]
    public void ApplicationNamesAreResolvedFromTheHostsInstalledApplications()
    {
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        var reader = new FakeReader(
            new RegistryTreeNode("HKLM", uninstall, "64", ["{A}"], []),
            new RegistryTreeNode("HKLM", uninstall + @"\{A}", "64", [], [Sz("DisplayName", "Vendor Suite"), Sz("Publisher", "Vendor")]),
            new RegistryTreeNode("HKLM", @"Software\Vendor\Suite", "64", [], [Sz("Url", "https://vendor")]));
        var runner = new MultiServerRunner(Path.Combine(_tmp.FullName, "cache")) { RegistryReaderFactory = _ => reader };

        var response = runner.Run([Plan("a", new MultiServerRegistry(["Vendor Suite"], null, null))], cancellationToken: TestContext.Current.CancellationToken);

        reader.Reads[0].Depth.Should().Be(1);
        response.Comparison.Files.Select(file => file.Path).Should().Contain(@"registry/HKLM (64-bit)/Software/Vendor/Suite.reg");
    }

    [Fact]
    public void ARegistryOnlyHostFailsWhenTheRegistryCannotBeRead()
    {
        var withRoot = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "files")).FullName;
        var runner = new MultiServerRunner(Path.Combine(_tmp.FullName, "cache")) { RegistryReaderFactory = _ => null };
        var registry = new MultiServerRegistry([@"HKLM\SOFTWARE\Vendor", "HKXX\\Bad"], null, null);

        var response = runner.Run([Plan("only", registry), Plan("both", registry, withRoot)], cancellationToken: TestContext.Current.CancellationToken);

        response.Results[0].Status.Should().Be(ServerScanStatus.Failed);
        response.Results[0].Message.Should().Contain("reading the registry needs Windows");
        response.Results[1].Status.Should().Be(ServerScanStatus.Succeeded);
    }

    [Fact]
    public void AReaderErrorIsReportedAndInvalidKeysAreNamed()
    {
        var runner = new MultiServerRunner(Path.Combine(_tmp.FullName, "cache")) { RegistryReaderFactory = _ => new ThrowingReader() };
        var response = runner.Run([Plan("a", new MultiServerRegistry([@"HKLM\SOFTWARE\Vendor"], "down", null))], cancellationToken: TestContext.Current.CancellationToken);
        response.Results[0].Message.Should().Be("Evaluated 0 configuration(s). Registry read failed: down: WinRM cannot complete the operation.");

        var ok = new MultiServerRunner(Path.Combine(_tmp.FullName, "cache2")) { RegistryReaderFactory = _ => new FakeReader() };
        var named = ok.Run([Plan("a", new MultiServerRegistry(["HKXX\\Bad"], null, null))], cancellationToken: TestContext.Current.CancellationToken);
        named.Results[0].Message.Should().EndWith(@"Not registry keys: HKXX\Bad.");
    }

    private sealed class ThrowingReader : IRegistryTreeReader
    {
        public RegistryTreeRead Read(IReadOnlyList<RegistryRoot> roots, int maxDepth, CancellationToken cancellationToken) =>
            throw new IOException("down: WinRM cannot complete the operation.");
    }

    [Fact]
    public void PlansCarryRegistrySettingsFromTheGuiModelAndTheJsonRequest()
    {
        var fromGui = MultiServerPlan.FromServerScanPlan(new ServerScanPlan
        {
            HostId = "a",
            Registry = new ServerScanRegistryOptions { Keys = [" HKLM\\SOFTWARE\\X ", "", "hklm\\software\\x"], Computer = " app-01 ", CredentialFile = " " },
        });
        fromGui.Registry!.Keys.Should().Equal(@"HKLM\SOFTWARE\X");
        fromGui.Registry.Computer.Should().Be("app-01");
        fromGui.Registry.CredentialFile.Should().BeNull();

        EngineJson.TryLoads("""{"plans": [{"host_id": "a", "registry": {"keys": ["Vendor"], "credential_file": "c.xml"}}, {"host_id": "b", "registry": "x"}]}""", out var request).Should().BeTrue();
        var plans = MultiServerPlan.BuildPlans(request);
        plans[0].Registry!.Keys.Should().Equal("Vendor");
        plans[0].Registry!.CredentialFile.Should().Be("c.xml");
        plans[0].Registry!.Computer.Should().BeNull();
        plans[1].Registry.Should().BeNull();
    }

    [Fact]
    public void TheWriterRendersRegeditsExportFormat()
    {
        var root = new RegistryRoot("HKLM", @"SOFTWARE\Vendor");
        RegistryTreeNode[] nodes =
        [
            new("HKLM", @"SOFTWARE\Vendor", null, ["b", "A"],
            [
                Sz("zeta", "C:\\x \"q\""),
                Sz("", "default"),
                Dword("Port", 443),
                new("Path", RegistryValueDecoder.RegExpandSz, Encoding.Unicode.GetBytes("%TMP%\0")),
                new("Blob", RegistryValueDecoder.RegBinary, Enumerable.Range(0, 30).Select(value => (byte)value).ToArray()),
            ]),
            new("HKLM", @"SOFTWARE\Vendor\A", null, [], []),
            new("HKLM", @"SOFTWARE\Vendor\b", null, [], [new("Q", RegistryValueDecoder.RegQword, BitConverter.GetBytes(1UL))]),
        ];

        var text = RegistryExportWriter.Render(root, nodes);

        text.Should().Be("""
            Windows Registry Editor Version 5.00

            [HKEY_LOCAL_MACHINE\SOFTWARE\Vendor]
            @="default"
            "Blob"=hex:00,01,02,03,04,05,06,07,08,09,0a,0b,0c,0d,0e,0f,10,11,12,13,14,15,\
              16,17,18,19,1a,1b,1c,1d
            "Path"=hex(2):25,00,54,00,4d,00,50,00,25,00,00,00
            "Port"=dword:000001bb
            "zeta"="C:\\x \"q\""

            [HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\A]

            [HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\b]
            "Q"=hex(b):01,00,00,00,00,00,00,00

            """.ReplaceLineEndings("\n"));
        RegistryExportWriter.Render(new RegistryRoot("HKLM", "Missing"), nodes).Should().Be("Windows Registry Editor Version 5.00\n");
    }

    [Fact]
    public void RemoteRepliesBecomeNodesAndErrorsBecomeExceptions()
    {
        var original = RemoteRegistryTreeReader.RunPowerShell;
        try
        {
            string? script = null;
            RemoteRegistryTreeReader.RunPowerShell = (text, environment, _) =>
            {
                script = text;
                JsonNode.Parse(environment["DRIFTBUSTER_REGISTRY_REQUEST"])!["computer"]!.GetValue<string>().Should().Be("app-01");
                return ("""noise{"truncated":true,"nodes":[{"hive":"HKLM","path":"S\\V","view":null,"subkeys":"Child","values":[{"name":"S","kind":"String","data":"x"},{"name":"E","kind":"ExpandString","data":"%A%"},{"name":"D","kind":"DWord","data":443},{"name":"Q","kind":"QWord","data":5},{"name":"M","kind":"MultiString","data":["a","b"]},{"name":"B","kind":"Binary","data":[1,2]},{"name":"M1","kind":"MultiString","data":"solo"}]}]}""".Replace("noise", "noise\n", StringComparison.Ordinal), string.Empty, 0);
            };

            var read = new RemoteRegistryTreeReader("app-01", null).Read([new RegistryRoot("HKLM", @"S\V")], 12, TestContext.Current.CancellationToken);

            script.Should().Contain("New-PSSession").And.Contain("$Request.roots");
            read.Truncated.Should().BeTrue();
            var node = read.Nodes.Should().ContainSingle().Subject;
            node.Subkeys.Should().Equal("Child");
            RegistryExportWriter.Render(new RegistryRoot("HKLM", @"S\V"), read.Nodes).Should().Contain("\"D\"=dword:000001bb").And.Contain("\"M\"=hex(7):61,00,00,00,62,00,00,00,00,00").And.Contain("\"S\"=\"x\"").And.Contain("\"B\"=hex:01,02");

            RemoteRegistryTreeReader.RunPowerShell = (_, _, _) => ("""{"error":"Access is denied."}""", string.Empty, 1);
            var denied = () => new RemoteRegistryTreeReader("app-01", "c.xml").Read([], 12, TestContext.Current.CancellationToken);
            denied.Should().Throw<IOException>().WithMessage("app-01: Access is denied.");

            RemoteRegistryTreeReader.RunPowerShell = (_, _, _) => (string.Empty, "boom", 1);
            var crashed = () => new RemoteRegistryTreeReader("app-01", null).Read([], 12, TestContext.Current.CancellationToken);
            crashed.Should().Throw<IOException>().WithMessage("app-01: boom");
        }
        finally
        {
            RemoteRegistryTreeReader.RunPowerShell = original;
        }
    }

    [Fact]
    public void TheRemoteDumpIsTheSameScriptTheOfflineRunnerUses()
    {
        var runner = File.ReadAllText(Path.Combine(RepoPaths.Root, "scripts", "driftbuster-offline-runner.ps1")).ReplaceLineEndings("\n");
        using var stream = typeof(RemoteRegistryTreeReader).Assembly.GetManifestResourceStream(RemoteRegistryTreeReader.DumpResource)!;
        var dump = new StreamReader(stream).ReadToEnd().ReplaceLineEndings("\n").Trim();

        runner.Should().Contain("$script:DBRemoteRegistryDump = " + dump + "\n");
    }
}
