using DriftBuster.Backend.Detection;

namespace DriftBuster.Backend.Tests.Detection;

/// <summary>
/// Walk entries the Python mirror does not cover: FIFOs, sockets and devices (never regular files, never opened), a file root
/// spelled with a trailing separator (<c>Path(root)</c> drops it) and a name the runtime cannot decode (reported, not dropped).
/// </summary>
public sealed class DetectorSpecialEntriesTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-detector-special-");

    public void Dispose() => SpecialFiles.DeleteTree(_tmp);

    private static List<(string Path, Exception Error)> Errors(out Action<string, Exception> onError)
    {
        var errors = new List<(string Path, Exception Error)>();
        onError = (path, error) => errors.Add((path, error));
        return errors;
    }

    [Fact(Timeout = 30_000)]
    public async Task ScanPathSkipsFifosSocketsAndDevicesWithoutOpeningThem()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "FIFOs and Unix sockets in a walked tree are a Linux case");
        using var special = SpecialFiles.Create(_tmp.FullName);
        File.WriteAllText(Path.Combine(_tmp.FullName, "regular.conf"), "alpha one\nbeta two\n");
        var detector = new Detector();

        var results = await Task.Run(() => detector.ScanPath(_tmp.FullName), TestContext.Current.CancellationToken);

        results.Select(result => Path.GetFileName(result.Path)).Should().Equal("regular.conf");
        foreach (var path in special.All)
        {
            var scanFile = () => detector.ScanFile(path);
            scanFile.Should().Throw<FileNotFoundException>(path);
            var scanRoot = () => detector.ScanPath(path);
            scanRoot.Should().Throw<DetectorIOException>("a root that is neither a file nor a directory does not exist for scan_path");
        }
    }

    [Fact]
    public void ScanPathSpellsAFileRootAsPathDoes()
    {
        var file = Path.Combine(_tmp.FullName, "readable.conf");
        File.WriteAllText(file, "alpha one\nbeta two\n");
        var errors = Errors(out var onError);
        var detector = new Detector(onError: onError);

        var results = detector.ScanPath(file + "/");
        var dotted = detector.ScanPath(_tmp.FullName + "//./readable.conf");

        errors.Should().BeEmpty();
        results.Should().ContainSingle().Which.Path.Should().Be(file);
        dotted.Should().ContainSingle().Which.Path.Should().Be(file);
    }

    [Fact(Timeout = 30_000)]
    public async Task ScanPathReportsAnUndecodableNameInsteadOfDroppingIt()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "file names are bytes only on Linux here");
        SpecialFiles.CreateUndecodableName(_tmp.FullName, "alpha one\nbeta two\n");
        File.WriteAllText(Path.Combine(_tmp.FullName, "good.conf"), "alpha one\nbeta two\n");
        var errors = Errors(out var onError);
        var detector = new Detector(onError: onError);

        var scan = () => detector.ScanPath(_tmp.FullName);

        (await Task.Run(() => scan.Should().Throw<DetectorIOException>().Which, TestContext.Current.CancellationToken))
            .Message.Should().Contain("not valid UTF-8");
        errors.Should().ContainSingle().Which.Path.Should().Be(Path.Combine(_tmp.FullName, "bad-�.ini"));
    }

    // CPython: Detector().scan_path raises "DetectorIOError .../rdir/x.conf: [Errno 13] Permission denied" (is_file() raising
    // inside a directory that can be listed but not searched), after handing it to the error handler.
    [Fact]
    public void ScanPathReportsAFileWhoseStatIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess, "search permission is observable only for a non-root Linux user");
        File.WriteAllText(Path.Combine(_tmp.FullName, "ok.conf"), "alpha one\nbeta two\n");
        var file = SpecialFiles.CreateUnsearchableDirectory(_tmp.FullName, "rdir", "alpha one\nbeta two\n");
        var errors = Errors(out var onError);
        var detector = new Detector(onError: onError);

        var scan = () => detector.ScanPath(_tmp.FullName);

        scan.Should().Throw<DetectorIOException>().Which.Message.Should().Contain("Permission denied");
        errors.Should().ContainSingle().Which.Path.Should().Be(file);
    }
}
