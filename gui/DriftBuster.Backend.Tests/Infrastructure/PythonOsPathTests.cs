using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <c>posixpath</c> and <c>ntpath</c> <c>expandvars</c> / <c>expanduser</c>; expected values were produced by CPython 3.13 with the
/// environment <c>HOME=/home/tester/</c>, <c>APP=/opt/app</c>, <c>EMPTY=</c> and <c>pwd.getpwnam</c> / <c>pwd.getpwuid</c> patched
/// to the accounts below; the surrogateescape decodes are CPython's <c>bytes.decode("utf-8", "surrogateescape")</c>.
/// </summary>
[Collection(PythonOsPathSeamCollection.Name)]
public sealed class PythonOsPathTests : IDisposable
{
    private static readonly Dictionary<string, string> Environment = new(StringComparer.Ordinal)
    {
        ["HOME"] = "/home/tester/",
        ["APP"] = "/opt/app",
        ["EMPTY"] = string.Empty,
        ["TILDE"] = "~/sub",
    };

    // The password database the seams answer from: pwd.getpwnam(name).pw_dir, and pwd.getpwuid(os.getuid()).pw_dir.
    private static readonly Dictionary<string, string> Accounts = new(StringComparer.Ordinal)
    {
        ["tester2"] = "/srv/tester2/",
        ["slash"] = "/",
    };

    private readonly Func<string, string?> _original = PythonOsPath.GetEnvironmentVariable;
    private readonly Func<byte[], string?> _originalUserHome = PythonOsPath.GetUserHome;
    private readonly Func<string?> _originalCurrentUserHome = PythonOsPath.GetCurrentUserHome;
    private readonly List<string> _lookedUp = [];
    private string? _currentUserHome = "/home/pwd-entry/";

    public PythonOsPathTests()
    {
        PythonOsPath.GetEnvironmentVariable = name => Environment.GetValueOrDefault(name);
        PythonOsPath.GetUserHome = name =>
        {
            var text = System.Text.Encoding.UTF8.GetString(name);
            _lookedUp.Add(text);
            return Accounts.GetValueOrDefault(text);
        };
        PythonOsPath.GetCurrentUserHome = () => _currentUserHome;
    }

    public void Dispose()
    {
        PythonOsPath.GetEnvironmentVariable = _original;
        PythonOsPath.GetUserHome = _originalUserHome;
        PythonOsPath.GetCurrentUserHome = _originalCurrentUserHome;
    }

    [Theory]
    [InlineData("$APP/x", "/opt/app/x")]
    [InlineData("${APP}/x", "/opt/app/x")]
    [InlineData("${APP", "${APP")]
    [InlineData("${}", "${}")]
    [InlineData("$MISSING/y", "$MISSING/y")]
    [InlineData("$$APP", "$/opt/app")]
    [InlineData("a$", "a$")]
    [InlineData("$EMPTY/z", "/z")]
    [InlineData("%APP%", "%APP%")]
    [InlineData("no-vars", "no-vars")]
    public void PosixExpandVars(string path, string expected)
        => PythonOsPath.ExpandVars(path, windows: false).Should().Be(expected);

    [Theory]
    [InlineData("%APP%\\x", "/opt/app\\x")]
    [InlineData("$APP", "/opt/app")]
    [InlineData("${APP}", "/opt/app")]
    [InlineData("$$", "$")]
    [InlineData("%%", "%")]
    [InlineData("%MISSING%", "%MISSING%")]
    [InlineData("%APP", "%APP")]
    [InlineData("'%APP%'", "'%APP%'")]
    [InlineData("%APP%%APP%", "/opt/app/opt/app")]
    [InlineData("$-A", "$-A")]
    [InlineData("${APP", "${APP")]
    [InlineData("plain", "plain")]
    public void WindowsExpandVars(string path, string expected)
        => PythonOsPath.ExpandVars(path, windows: true).Should().Be(expected);

    [Theory]
    [InlineData("~", "/home/tester")]
    [InlineData("~/cfg", "/home/tester/cfg")]
    [InlineData("~other/cfg", "~other/cfg")]
    [InlineData("~tester2/cfg", "/srv/tester2/cfg")]
    [InlineData("~tester2", "/srv/tester2")]
    [InlineData("~tester2/", "/srv/tester2/")]
    [InlineData("~slash/x", "/x")]
    [InlineData("~slash", "/")]
    [InlineData("~slash/", "/")]
    [InlineData("cfg/~", "cfg/~")]
    public void PosixExpandUser(string path, string expected)
        => PythonOsPath.ExpandUser(path, windows: false).Should().Be(expected);

    [Fact]
    public void PosixExpandUserWithoutHomeReadsTheCurrentUsersEntry()
    {
        Environment.Remove("HOME");
        try
        {
            PythonOsPath.ExpandUser("~/cfg", windows: false).Should().Be("/home/pwd-entry/cfg");
            PythonOsPath.ExpandUser("~", windows: false).Should().Be("/home/pwd-entry");
            _currentUserHome = null;
            PythonOsPath.ExpandUser("~/cfg", windows: false).Should().Be("~/cfg", "getpwuid raising KeyError leaves the path unchanged");
        }
        finally
        {
            Environment["HOME"] = "/home/tester/";
        }
    }

    [Fact]
    public void PosixExpandUserOfANameHoldingANulRaisesValueError()
        => FluentActions.Invoking(() => PythonOsPath.ExpandUser("~a\0b/x", windows: false))
            .Should().Throw<PythonValueException>().WithMessage("embedded null byte");

    [Fact]
    public void PosixExpandUserNeverLooksUpANameHoldingAnUnpairedSurrogate()
    {
        // CPython raises UnicodeEncodeError for '\ud800' and looks '\udcff' up as byte 0xFF; decision S keeps the path unchanged
        // and never hands the database a guessed spelling. Attribute data cannot hold a lone surrogate, so the paths are built here.
        foreach (var path in new[] { "~\ud800/x", "~a\udcff" })
        {
            PythonOsPath.ExpandUser(path, windows: false).Should().Be(path);
        }

        _lookedUp.Should().BeEmpty();
    }

    [Fact]
    public void PosixExpandUserReadsThePasswordDatabaseOfThisHost()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the password database is read through getpwnam_r on Linux only");
        var user = System.Environment.UserName;
        var line = File.ReadLines("/etc/passwd").FirstOrDefault(entry => entry.StartsWith(user + ":", StringComparison.Ordinal));
        Assert.SkipWhen(line is null, $"the current user '{user}' has no /etc/passwd entry to compare with");

        PythonOsPath.GetUserHome = _originalUserHome;
        PythonOsPath.GetCurrentUserHome = _originalCurrentUserHome;
        var home = line!.Split(':')[5].TrimEnd('/');
        PythonOsPath.ExpandUser($"~{user}/cfg", windows: false).Should().Be(home + "/cfg");
        PythonOsPath.ExpandUser("~driftbuster-no-such-user/x", windows: false).Should().Be("~driftbuster-no-such-user/x");
        Environment.Remove("HOME");
        try
        {
            PythonOsPath.ExpandUser("~/cfg", windows: false).Should().Be(home + "/cfg");
        }
        finally
        {
            Environment["HOME"] = "/home/tester/";
        }
    }

    [Fact]
    public void PasswordEntriesDecodeWithSurrogateEscape()
    {
        var cases = new (byte[] Bytes, string Expected)[]
        {
            ([0x61, 0xFF, 0x62], "a\udcffb"),
            ([0xE2, 0x28], "\udce2("),
            ([0xE2, 0x82], "\udce2\udc82"),
            ([0xED, 0xA0, 0x80, 0x78], "\udced\udca0\udc80x"),
            ([0xF4, 0x90, 0x80, 0x80], "\udcf4\udc90\udc80\udc80"),
            ([0x2F, 0xC3, 0xA9], "/\u00e9"),
        };
        foreach (var (bytes, expected) in cases)
        {
            UnixPasswd.DecodeSurrogateEscape(bytes).Should().Be(expected);
        }
    }

    [Fact]
    public void WindowsExpandUser()
    {
        Environment["USERPROFILE"] = @"C:\Users\tester";
        Environment["USERNAME"] = "tester";
        try
        {
            PythonOsPath.ExpandUser(@"~\cfg", windows: true).Should().Be(@"C:\Users\tester\cfg");
            PythonOsPath.ExpandUser("~tester/cfg", windows: true).Should().Be(@"C:\Users\tester/cfg");
            PythonOsPath.ExpandUser(@"~other\cfg", windows: true).Should().Be(OperatingSystem.IsWindows() ? @"C:\Users\other\cfg" : @"~other\cfg", "the profile directory is split with the host's path rules");
            Environment.Remove("USERPROFILE");
            PythonOsPath.ExpandUser("~", windows: true).Should().Be("~");
            Environment["HOMEPATH"] = "/Users/tester";
            Environment["HOMEDRIVE"] = "D:";
            PythonOsPath.ExpandUser("~", windows: true).Should().Be("D:/Users/tester");
        }
        finally
        {
            Environment.Remove("USERPROFILE");
            Environment.Remove("USERNAME");
            Environment.Remove("HOMEPATH");
            Environment.Remove("HOMEDRIVE");
        }
    }
    // A structured run profile expands a source as offline_runner does (variables, then the user's home); run_profiles expands the home
    // first, so a variable holding "~" stays literal there.
    [Fact]
    public void StructuredRunProfileSourcesExpandVariablesBeforeTheHome()
    {
        DriftBuster.Backend.Profiles.Run.RunProfileExecutor.ExpandStructuredPath("$TILDE/x").Should().Be("/home/tester/sub/x");
        PythonOsPath.ExpandVars(PythonOsPath.ExpandUser("$TILDE/x")).Should().Be("~/sub/x");
    }
}
