using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <see cref="PythonOSError.Errno"/> names the errno Python's <c>open()</c> raises for the same path: a component that is a regular
/// file is <c>NotADirectoryError</c> (<c>[Errno 20]</c>), not the <c>FileNotFoundError</c> the runtime's exception type suggests.
/// </summary>
public sealed class PythonOSErrorTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-oserror-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Touch(string name)
    {
        var path = Path.Combine(_tmp.FullName, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void ErrnoOfAPathThroughARegularFileIsNotADirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the runtime reports ENOTDIR as a missing directory only on Unix; Windows maps ERROR_PATH_NOT_FOUND to ENOENT");
        var through = Path.Combine(Touch("afile"), "x");
        var read = () => File.ReadAllBytes(through);
        var write = () => File.WriteAllText(through, "t");
        PythonOSError.Errno(read.Should().Throw<IOException>().Which, through).Should().Be(PythonOSError.NotADirectory);
        PythonOSError.Errno(write.Should().Throw<IOException>().Which, through).Should().Be(PythonOSError.NotADirectory);

        // The runtime removes ".." lexically and reports "q" missing; the kernel (and Python's open) stops at the file first.
        var dotdot = Path.Combine(Touch("bfile"), "..", "q");
        var readDotDot = () => File.ReadAllBytes(PythonPath.KernelPath(dotdot));
        PythonOSError.Errno(readDotDot.Should().Throw<IOException>().Which, dotdot).Should().Be(PythonOSError.NotADirectory);
    }

    [Fact]
    public void ErrnoOfAPathThroughAMissingDirectoryIsNoSuchFile()
    {
        var through = Path.Combine(_tmp.FullName, "missing", "x");
        var read = () => File.ReadAllBytes(through);
        var raised = read.Should().Throw<IOException>().Which;
        PythonOSError.Errno(raised, through).Should().Be(PythonOSError.NoSuchFile);
        PythonOSError.Errno(raised).Should().Be(PythonOSError.NoSuchFile, "without a path the runtime's own answer stands");
    }

    [Fact]
    public void TextFileReadsAndWritesThroughAFileRaiseNotADirectoryError()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux errno texts");
        var through = Path.Combine(Touch("afile"), "x");
        var expected = PythonOSError.Create(PythonOSError.NotADirectory, through).Message;
        expected.Should().StartWith("[Errno 20] Not a directory: ");
        var read = () => PythonTextFile.ReadUtf8Text(through);
        var write = () => PythonTextFile.WriteText(through, "t");
        read.Should().Throw<IOException>().Which.Message.Should().Be(expected);
        write.Should().Throw<IOException>().Which.Message.Should().Be(expected);
        PythonOSError.TypeName(PythonOSError.NotADirectory).Should().Be("NotADirectoryError");
    }

    [Fact]
    public void LoadJsonThroughAFileReportsNotADirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux errno texts");
        var through = Path.Combine(Touch("store.json"), "x");
        var load = () => DetectionProfileCommands.LoadJson(through);
        load.Should().Throw<PythonValueException>().Which.Message.Should().Be(
            $"Unable to read JSON payload from {through}: {PythonOSError.Create(PythonOSError.NotADirectory, through).Message}");
    }

    // open() on a directory: the kernel's EISDIR on Unix; on Windows the CRT's _wopen fails with ERROR_ACCESS_DENIED, mapped to EACCES
    // (CPython 3.14 on Windows: PermissionError "[Errno 13] Permission denied: 'C:\\...'" for open(), Path.read_text and Path.write_text).
    [Fact]
    public void ReadingOrWritingADirectoryRaisesOpensErrorForThisPlatform()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "dir")).FullName;
        PythonOSError.DirectoryOpenErrno.Should().Be(OperatingSystem.IsWindows() ? PythonOSError.PermissionDenied : PythonOSError.IsADirectory);
        Action read = () => PythonTextFile.ReadUtf8Text(directory);
        Action write = () => PythonTextFile.WriteText(directory, "t");
        foreach (var act in new[] { read, write })
        {
            var raised = act.Should().Throw<IOException>().Which;
            raised.Message.Should().Be(OSErrorTexts.DirectoryOpen(directory));
            PythonOSError.TypeName(raised.HResult).Should().Be(OSErrorTexts.DirectoryOpenType);
        }
    }

    // CPython's [Errno N] text: strerror on Unix; on Windows the CRT's _sys_errlist entry (Python/errors.c reads it for 0 < N < _sys_nerr),
    // which is not FormatMessage of N as a Win32 code ("The system cannot find the file specified." for 2).
    [Theory]
    [InlineData(1, "Operation not permitted")]
    [InlineData(2, "No such file or directory")]
    [InlineData(4, "Interrupted function call")]
    [InlineData(7, "Arg list too long")]
    [InlineData(12, "Not enough space")]
    [InlineData(13, "Permission denied")]
    [InlineData(15, "Unknown error")]
    [InlineData(16, "Resource device")]
    [InlineData(17, "File exists")]
    [InlineData(18, "Improper link")]
    [InlineData(20, "Not a directory")]
    [InlineData(21, "Is a directory")]
    [InlineData(22, "Invalid argument")]
    [InlineData(25, "Inappropriate I/O control operation")]
    [InlineData(29, "Invalid seek")]
    [InlineData(33, "Domain error")]
    [InlineData(34, "Result too large")]
    [InlineData(36, "Resource deadlock avoided")]
    [InlineData(38, "Filename too long")]
    [InlineData(41, "Directory not empty")]
    [InlineData(42, "Illegal byte sequence")]
    public void WindowsStrErrorIsTheCrtTable(int errno, string text) => PythonOSError.WindowsStrError(errno).Should().Be(text);

    [Theory]
    [InlineData(0)]
    [InlineData(43)]
    [InlineData(138)]
    [InlineData(-1)]
    public void WindowsStrErrorOutsideTheCrtTableIsNull(int errno) => PythonOSError.WindowsStrError(errno).Should().BeNull();

    [Theory]
    [InlineData(2, "No such file or directory")]
    [InlineData(13, "Permission denied")]
    [InlineData(17, "File exists")]
    [InlineData(20, "Not a directory")]
    [InlineData(21, "Is a directory")]
    public void CreateSpellsTheErrnoAsCPythonDoesOnThisPlatform(int errno, string text)
    {
        PythonOSError.StrError(errno).Should().Be(text, "the C library and the CRT agree on these texts");
        PythonOSError.Create(errno).Message.Should().Be($"[Errno {errno}] {text}");
        PythonOSError.Create(errno, "a\\b").Message.Should().Be($"[Errno {errno}] {text}: 'a\\\\b'");
    }

    [Fact]
    public void StrErrorPastTheCrtTableOnWindowsIsTheTrimmedWin32Message()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FormatMessage");
        PythonOSError.StrError(258).Should().Be("The wait operation timed out");
        PythonOSError.StrError(41).Should().Be("Directory not empty");
    }

    // errnomap on Linux and on Windows (CPython 3.14 on Windows: type(OSError(N, "m")) for each N).
    [Theory]
    [InlineData(2, "FileNotFoundError", "FileNotFoundError")]
    [InlineData(13, "PermissionError", "PermissionError")]
    [InlineData(21, "IsADirectoryError", "IsADirectoryError")]
    [InlineData(11, "BlockingIOError", "BlockingIOError")]
    [InlineData(32, "BrokenPipeError", "BrokenPipeError")]
    [InlineData(36, "OSError", "OSError")]
    [InlineData(103, "ConnectionAbortedError", "OSError")]
    [InlineData(104, "ConnectionResetError", "OSError")]
    [InlineData(108, "BrokenPipeError", "OSError")]
    [InlineData(110, "TimeoutError", "OSError")]
    [InlineData(111, "ConnectionRefusedError", "OSError")]
    [InlineData(114, "BlockingIOError", "OSError")]
    [InlineData(138, "OSError", "TimeoutError")]
    [InlineData(10035, "OSError", "BlockingIOError")]
    [InlineData(10037, "OSError", "BlockingIOError")]
    [InlineData(10053, "OSError", "ConnectionAbortedError")]
    [InlineData(10054, "OSError", "ConnectionResetError")]
    [InlineData(10058, "OSError", "BrokenPipeError")]
    [InlineData(10060, "OSError", "TimeoutError")]
    [InlineData(10061, "OSError", "ConnectionRefusedError")]
    [InlineData(10004, "OSError", "OSError")]
    public void TypeNameFollowsThePlatformsErrnomap(int errno, string linux, string windows)
    {
        PythonOSError.TypeName(errno, windows: false).Should().Be(linux);
        PythonOSError.TypeName(errno, windows: true).Should().Be(windows);
        PythonOSError.TypeName(errno).Should().Be(OperatingSystem.IsWindows() ? windows : linux);
    }

    [Theory]
    [InlineData(2, PythonOSError.NoSuchFile)] // ERROR_FILE_NOT_FOUND
    [InlineData(3, PythonOSError.NoSuchFile)] // ERROR_PATH_NOT_FOUND
    [InlineData(206, PythonOSError.NoSuchFile)] // ERROR_FILENAME_EXCED_RANGE
    [InlineData(unchecked((int)0x80070003), PythonOSError.NoSuchFile)] // the HRESULT DirectoryNotFoundException carries
    [InlineData(267, PythonOSError.NotADirectory)] // ERROR_DIRECTORY
    [InlineData(unchecked((int)0x8007010B), PythonOSError.NotADirectory)]
    [InlineData(5, PythonOSError.PermissionDenied)] // ERROR_ACCESS_DENIED
    [InlineData(32, PythonOSError.PermissionDenied)] // ERROR_SHARING_VIOLATION
    [InlineData(35, PythonOSError.PermissionDenied)] // undefined, mapped as the CRT maps it
    [InlineData(80, PythonOSError.FileExists)]
    [InlineData(183, PythonOSError.FileExists)]
    [InlineData(145, 41)] // ERROR_DIR_NOT_EMPTY -> ENOTEMPTY (MSVC value; CPython on Windows: os.rmdir errno 41, winerror 145)
    [InlineData(258, 138)] // WAIT_TIMEOUT -> ETIMEDOUT (MSVC value)
    [InlineData(1113, 42)] // ERROR_NO_UNICODE_TRANSLATION -> EILSEQ (MSVC value)
    [InlineData(10004, 4)] // WSAEINTR -> EINTR
    [InlineData(10061, 10061)] // WSAECONNREFUSED stays a Winsock value
    [InlineData(87, PythonOSError.InvalidArgument)]
    [InlineData(999, PythonOSError.InvalidArgument)]
    public void WinErrorToErrnoFollowsCPythonsTable(int winerror, int errno) => PythonOSError.WinErrorToErrno(winerror).Should().Be(errno);
}
