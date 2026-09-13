using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary><see cref="PythonPath.IsFile"/> is <c>Path.is_file()</c>: a <c>stat</c> that follows links must say regular file.</summary>
public sealed class PythonPathTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-python-path-");

    public void Dispose() => SpecialFiles.DeleteTree(_tmp);

    [Fact]
    public void RegularFilesAndLinksToThemAreFiles()
    {
        var regular = Path.Combine(_tmp.FullName, "a.ini");
        File.WriteAllText(regular, "x");
        PythonPath.IsFile(regular).Should().BeTrue();
        PythonPath.IsFile(_tmp.FullName).Should().BeFalse();
        PythonPath.IsFile(Path.Combine(_tmp.FullName, "missing")).Should().BeFalse();
        PythonPath.IsFile(regular + "\0tail").Should().BeFalse();
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(_tmp.FullName, "link.ini"), "a.ini");
            File.CreateSymbolicLink(Path.Combine(_tmp.FullName, "dangling.ini"), "nowhere");
            PythonPath.IsFile(Path.Combine(_tmp.FullName, "link.ini")).Should().BeTrue();
            PythonPath.IsFile(Path.Combine(_tmp.FullName, "dangling.ini")).Should().BeFalse();
            PythonPath.IsFile(regular + "/").Should().BeFalse("stat of a file spelled as a directory fails with ENOTDIR");
        }
    }

    [Fact]
    public void FifosSocketsAndDevicesAreNotFiles()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "FIFOs and Unix sockets in a walked tree are a Linux case");
        using var special = SpecialFiles.Create(_tmp.FullName);

        foreach (var path in special.All)
        {
            PythonPath.IsFile(path).Should().BeFalse(path);
        }

        PythonPath.IsFile("/dev/null").Should().BeFalse();
    }

    [Fact]
    public void AnUndecodableNameIsRecognisedWithoutOpeningIt()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "file names are bytes only on Linux here");
        SpecialFiles.CreateUndecodableName(_tmp.FullName, "server host=bad.corp.local\n");
        File.WriteAllText(Path.Combine(_tmp.FullName, "good-\uFFFD.ini"), "x");

        var listed = Directory.GetFiles(_tmp.FullName).Order(StringComparer.Ordinal).ToList();

        listed.Should().HaveCount(2);
        listed.Where(PythonPath.IsUndecodableName).Select(Path.GetFileName).Should().Equal("bad-\uFFFD.ini");
        PythonPath.IsFile(Path.Combine(_tmp.FullName, "good-\uFFFD.ini")).Should().BeTrue();
    }

    // CPython 3.13 Path.is_file() ignores only ENOENT, ENOTDIR, EBADF and ELOOP (pathlib._abc._IGNORED_ERRNOS); a file inside a
    // directory that can be listed but not searched raises PermissionError: [Errno 13] Permission denied.
    [Fact]
    public void AStatThatFailsWithAnErrorPythonDoesNotIgnoreRaises()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess, "search permission is observable only for a non-root Linux user");
        var file = SpecialFiles.CreateUnsearchableDirectory(_tmp.FullName, "rdir", "alpha one\n");

        var isFile = () => PythonPath.IsFile(file);
        var undecodable = () => PythonPath.IsUndecodableName(Path.Combine(_tmp.FullName, "rdir", "bad-\uFFFD.ini"));

        isFile.Should().Throw<UnauthorizedAccessException>().WithMessage($"[Errno 13] Permission denied: '{file}'");
        undecodable.Should().Throw<UnauthorizedAccessException>();
        PythonPath.IsFile(Path.Combine(_tmp.FullName, "rdir")).Should().BeFalse();
        var tooLong = () => PythonPath.IsFile(Path.Combine(_tmp.FullName, new string('n', 300)));
        tooLong.Should().Throw<IOException>().WithMessage("[Errno 36] File name too long: '*'");
    }
}
