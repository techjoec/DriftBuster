using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// Python's texts for a file error once the file is open (no file name), for a path holding a NUL character (the <c>ValueError</c> the
/// first call on it raises) and for a <c>stat</c> error, which carries its errno. Swaps <see cref="PythonTextFile.OpenStream"/>, so it
/// runs alone.
/// </summary>
[Collection(ProcessWideSeamCollection.Name)]
public sealed class PythonTextFileErrorTests : IDisposable
{
    private readonly Func<string, FileMode, Stream> _originalOpen = PythonTextFile.OpenStream;
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-textfile-");

    public void Dispose()
    {
        PythonTextFile.OpenStream = _originalOpen;
        foreach (var directory in _tmp.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            if (!OperatingSystem.IsWindows())
            {
                directory.UnixFileMode |= UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            }
        }

        _tmp.Delete(recursive: true);
    }

    [Theory]
    [InlineData(28)]
    [InlineData(122)]
    [InlineData(5)]
    public void AWriteThatFailsAfterTheOpenNamesNoFile(int errno)
    {
        var path = Path.Combine(_tmp.FullName, "out.json");
        PythonTextFile.OpenStream = (_, _) => new FailingStream(new IOException("runtime text", errno));
        var write = () => PythonTextFile.WriteText(path, "payload");
        var raised = write.Should().Throw<IOException>().Which;
        raised.Message.Should().Be($"[Errno {errno}] {PythonOSError.StrError(errno)}");
        raised.HResult.Should().Be(errno);
    }

    [Fact]
    public void AReadThatFailsAfterTheOpenNamesNoFile()
    {
        var path = Path.Combine(_tmp.FullName, "in.json");
        File.WriteAllText(path, "{}");
        PythonTextFile.OpenStream = (_, _) => new FailingStream(new IOException("runtime text", 5));
        var read = () => PythonTextFile.ReadUtf8Text(path);
        read.Should().Throw<IOException>().WithMessage($"[Errno 5] {PythonOSError.StrError(5)}");

        var load = () => DetectionProfileCommands.LoadJson(path);
        load.Should().Throw<PythonValueException>().WithMessage($"Unable to read JSON payload from {path}: [Errno 5] {PythonOSError.StrError(5)}");
    }

    [Fact]
    public void AnOpenThatFailsStillNamesTheFile()
    {
        var path = Path.Combine(_tmp.FullName, "in.json");
        PythonTextFile.OpenStream = (_, _) => throw new FileNotFoundException("runtime text", path);
        var read = () => PythonTextFile.ReadUtf8Text(path);
        read.Should().Throw<IOException>().WithMessage($"[Errno 2] {PythonOSError.StrError(2)}: {PythonRepr.StrRepr(path)}");
    }

    [Fact]
    public void AFileTooLargeForTheWriteIsEfbig()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the runtime reports EFBIG as ArgumentOutOfRangeException on Unix only");
        PythonTextFile.OpenStream = (_, _) => new FailingStream(new ArgumentOutOfRangeException(paramName: null, message: "File length too big."));
        var write = () => PythonTextFile.WriteText(Path.Combine(_tmp.FullName, "big.json"), "payload");
        write.Should().Throw<IOException>().WithMessage($"[Errno 27] {PythonOSError.StrError(27)}");
    }

    [Fact]
    public void WritingThroughALinkToDevFullIsNoSpaceWithoutAFileName()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/dev/full"), "/dev/full is a Linux device");
        var link = Path.Combine(_tmp.FullName, "full.json");
        File.CreateSymbolicLink(link, "/dev/full");
        var write = () => PythonTextFile.WriteText(link, new string('x', 10));
        write.Should().Throw<IOException>().WithMessage("[Errno 28] No space left on device");
    }

    [Fact]
    public void APathHoldingANulRaisesTheFirstCallsValueError()
    {
        var nul = Path.Combine(_tmp.FullName, "a\0b");
        var open = OperatingSystem.IsWindows() ? "embedded null character" : "embedded null byte";
        var write = () => PythonTextFile.WriteText(nul, "t");
        write.Should().Throw<PythonValueException>().WithMessage(open);
        var read = () => PythonTextFile.ReadUtf8Text(nul);
        read.Should().Throw<PythonValueException>().WithMessage(open);
        var load = () => DetectionProfileCommands.LoadJson(nul);
        load.Should().Throw<PythonValueException>().WithMessage(open, "_load_json wraps only OSError");
        var mkdir = () => PythonPath.MakeDirectories(nul);
        mkdir.Should().Throw<PythonValueException>().WithMessage("mkdir: embedded null character in path");
        RunProfileStore.Exists(nul).Should().BeFalse("Path.exists() reads the ValueError as False");

        if (!OperatingSystem.IsWindows())
        {
            var resolve = () => PythonPath.Resolve("tree\0");
            resolve.Should().Throw<PythonValueException>().WithMessage("lstat: embedded null character in path");
        }
    }

    [Fact]
    public void StatErrorsCarryTheirErrno()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("statx is the Linux probe");
            return;
        }

        var tooLong = Path.Combine(_tmp.FullName, new string('n', 300));
        var exists = () => RunProfileStore.Exists(tooLong);
        var raised = exists.Should().Throw<IOException>().Which;
        raised.HResult.Should().Be(PythonOSError.NameTooLong);
        raised.Message.Should().Be($"[Errno 36] File name too long: {PythonRepr.StrRepr(tooLong)}");
        PythonOSError.Errno(raised).Should().Be(PythonOSError.NameTooLong);
        PythonOSError.TypeName(raised.HResult).Should().Be("OSError");

        Assert.SkipWhen(string.Equals(Environment.UserName, "root", StringComparison.Ordinal), "root searches every directory");
        var locked = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "locked"));
        locked.UnixFileMode = UnixFileMode.UserRead;
        var refused = () => RunProfileStore.Exists(Path.Combine(locked.FullName, "x"));
        var denied = refused.Should().Throw<UnauthorizedAccessException>().Which;
        PythonOSError.Errno(denied).Should().Be(PythonOSError.PermissionDenied);
        denied.InnerException!.HResult.Should().Be(PythonOSError.PermissionDenied);
    }

    // A stream the open hands back that fails every read and write, as a full disk or a failing device does.
    private sealed class FailingStream(Exception error) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw error;

        public override void Write(ReadOnlySpan<byte> buffer) => throw error;

        public override int Read(byte[] buffer, int offset, int count) => throw error;

        public override int Read(Span<byte> buffer) => throw error;

        public override void CopyTo(Stream destination, int bufferSize) => throw error;
    }
}
