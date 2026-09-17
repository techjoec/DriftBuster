using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Remote;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <c>str(Path(path).expanduser())</c> (CPython 3.13): the first name is expanded with <c>os.path.expanduser</c>, and a name still starting
/// with <c>~</c> afterwards raises <c>RuntimeError("Could not determine home directory.")</c>. The password database and environment are the
/// seams' accounts below.
/// </summary>
[Collection(PythonOsPathSeamCollection.Name)]
public sealed class PythonPathExpandUserTests : IDisposable
{
    private const string NoHome = "Could not determine home directory.";

    private readonly Func<string, string?> _originalEnvironment = PythonOsPath.GetEnvironmentVariable;
    private readonly Func<byte[], string?> _originalUserHome = PythonOsPath.GetUserHome;
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal) { ["HOME"] = "/home/tester/" };
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-expanduser-");

    public PythonPathExpandUserTests()
    {
        PythonOsPath.GetEnvironmentVariable = name => _environment.GetValueOrDefault(name);
        PythonOsPath.GetUserHome = name => string.Equals(System.Text.Encoding.UTF8.GetString(name), "known", StringComparison.Ordinal) ? "/srv/known/" : null;
    }

    public void Dispose()
    {
        PythonOsPath.GetEnvironmentVariable = _originalEnvironment;
        PythonOsPath.GetUserHome = _originalUserHome;
        _tmp.Delete(recursive: true);
    }

    [Theory]
    [InlineData("~", "/home/tester")]
    [InlineData("~/x//y/", "/home/tester/x/y")]
    [InlineData("~known/x", "/srv/known/x")]
    [InlineData("a/~nobody", "a/~nobody")]
    [InlineData("/~nobody", "/~nobody")]
    [InlineData("", ".")]
    public void PosixPathsExpandTheirFirstName(string path, string expected)
        => PythonPath.ExpandUser(path, windows: false).Should().Be(expected);

    [Theory]
    [InlineData("~nobody")]
    [InlineData("~nobody/x.json")]
    [InlineData("./~nobody/x")]
    public void AnUnknownAccountCannotDetermineTheHomeDirectory(string path)
    {
        var expand = () => PythonPath.ExpandUser(path, windows: false);
        expand.Should().Throw<PythonRuntimeException>().WithMessage(NoHome);
    }

    [Fact]
    public void ANulInTheAccountNameIsPwdsValueError()
    {
        var expand = () => PythonPath.ExpandUser("~a\0b/x", windows: false);
        expand.Should().Throw<PythonValueException>().WithMessage("embedded null byte");
    }

    [Fact]
    public void WindowsPathsUseTheProfileAndRefuseAnotherUserWithoutAMatchingUserName()
    {
        _environment["USERPROFILE"] = @"C:\Users\me";
        PythonPath.ExpandUser("~/sub", windows: true).Should().Be(@"C:\Users\me\sub");
        var other = () => PythonPath.ExpandUser(@"~x\y", windows: true);
        other.Should().Throw<PythonRuntimeException>().WithMessage(NoHome);
        if (OperatingSystem.IsWindows())
        {
            // ntpath's sibling guess splits the profile with the host's separators.
            _environment["USERNAME"] = "me";
            PythonPath.ExpandUser(@"~x\y", windows: true).Should().Be(@"C:\Users\x\y");
        }

        PythonPath.ExpandUser(@"C:~x", windows: true).Should().Be(@"C:~x");
    }

    [Fact]
    public void CaptureExportSqlRefusesAnUnknownAccountBeforeCreatingAnything()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the posix account lookup is the seam");
        var database = Path.Combine(_tmp.FullName, "x.sqlite");
        File.WriteAllBytes(database, []);
        var output = () => CaptureRunner.RunSqlExport(
            new SqlExportOptions { Database = [database], OutputDir = "~nobody-driftbuster/out" }, TextWriter.Null, TextWriter.Null);
        output.Should().Throw<PythonRuntimeException>().WithMessage(NoHome);
        Directory.Exists("~nobody-driftbuster").Should().BeFalse();

        var outputDir = Path.Combine(_tmp.FullName, "sql");
        var stdout = new StringWriter();
        var second = () => CaptureRunner.RunSqlExport(
            new SqlExportOptions { Database = [database, "~nobody-driftbuster/x.sqlite"], OutputDir = outputDir }, stdout, TextWriter.Null);
        second.Should().Throw<PythonRuntimeException>().WithMessage(NoHome);
        File.Exists(Path.Combine(outputDir, "sql-manifest.json")).Should().BeFalse("the error ends the command before the manifest");

        var scans = () => CaptureRunner.LoadRegistryScanSummaries(["~nobody-driftbuster/x.json"]);
        scans.Should().Throw<PythonRuntimeException>().WithMessage(NoHome);
    }
}
