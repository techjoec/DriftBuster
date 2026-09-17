using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <see cref="PythonNtRealPath"/> (CPython 3.13 <c>ntpath.realpath</c>, <c>strict=False</c>) over a fake <see cref="INtPathSystem"/>
/// modelled on the Windows calls, so the algorithm's branches run on every host: the OS naming the whole path (case, 8.3 names and
/// mapped drives come back as the OS spells them), the non-strict walk up a path the OS cannot name, links followed by hand where their
/// target is missing, the stored name read where access is refused, the <c>\\?\</c> prefix rules and the <c>nul</c> device. Each
/// expectation is traced through <c>ntpath.py</c> (<c>realpath</c>, <c>_getfinalpathname_nonstrict</c>, <c>_readlink_deep</c>).
/// </summary>
public sealed class PythonNtRealPathTests
{
    private sealed class FakeSystem : INtPathSystem
    {
        // Keyed by the upper-cased path the OS is asked to open: an error wins, then a final name; anything else fails with 2.
        public Dictionary<string, string> Final { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Errors { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Links { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Symlinks { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> StoredNames { get; } = new(StringComparer.Ordinal);

        public List<string> Opened { get; } = [];

        public string CurrentDirectory => @"C:\Work";

        public string GetFinalPathName(string path)
        {
            Opened.Add(path);
            if (path.Contains('\0', StringComparison.Ordinal))
            {
                throw new PythonValueException("embedded null character", nameof(path));
            }

            var key = Key(path);
            if (Errors.TryGetValue(key, out var error))
            {
                throw new NtPathException(error, "CreateFileW", path);
            }

            return Final.TryGetValue(key, out var final) ? final : throw new NtPathException(2, "CreateFileW", path);
        }

        public string ReadLink(string path)
            => Links.TryGetValue(Key(path), out var target) ? target : throw new NtPathException(4390, "readlink", path);

        public bool IsLink(string path) => Symlinks.Contains(Key(path));

        public string FindFirstFile(string path)
            => StoredNames.TryGetValue(Key(path), out var name) ? name : throw new NtPathException(2, "FindFirstFileW", path);

        private static string Key(string path) => path.Replace('/', '\\').ToUpperInvariant();
    }

    [Fact]
    public void ThePathTheOsNamesIsReturnedWithoutThePrefix()
    {
        var system = new FakeSystem();
        system.Final[@"C:\WORK\MIXEDCASE\PROGRA~1\X.TXT"] = @"\\?\C:\Work\MixedCase\Program Files\x.txt";
        system.Final[@"C:\WORK\MIXEDCASE\PROGRAM FILES\X.TXT"] = @"\\?\C:\Work\MixedCase\Program Files\x.txt";

        PythonNtRealPath.RealPath(@"mixedcase/PROGRA~1/./x.txt", system).Should().Be(@"C:\Work\MixedCase\Program Files\x.txt");
    }

    [Fact]
    public void AMappedDriveIsReplacedByTheUncPathItMaps()
    {
        var system = new FakeSystem();
        system.Final[@"Z:\DB.SQLITE"] = @"\\?\UNC\server\share\db.sqlite";
        system.Final[@"\\SERVER\SHARE\DB.SQLITE"] = @"\\?\UNC\server\share\db.sqlite";

        PythonNtRealPath.RealPath(@"Z:\db.sqlite", system).Should().Be(@"\\server\share\db.sqlite");
    }

    [Fact]
    public void ThePrefixIsKeptWhenTheShorterSpellingNamesAnotherPathOrWasGiven()
    {
        var kept = new FakeSystem();
        kept.Final[@"C:\A\B"] = @"\\?\C:\a\b.";
        kept.Final[@"C:\A\B."] = @"\\?\C:\a\b";
        PythonNtRealPath.RealPath(@"C:\a\b", kept).Should().Be(@"\\?\C:\a\b.");

        var given = new FakeSystem();
        given.Final[@"\\?\C:\A\B"] = @"\\?\C:\a\b";
        PythonNtRealPath.RealPath(@"\\?\C:\a\b", given).Should().Be(@"\\?\C:\a\b");
        given.Opened.Should().ContainSingle("a path given with the prefix is neither joined nor re-opened without it");
    }

    [Fact]
    public void AMissingTailIsJoinedOntoTheLongestPrefixTheOsNames()
    {
        var system = new FakeSystem();
        system.Final[@"C:\WORK\REAL"] = @"\\?\C:\Work\Real";

        // The stripped spelling fails as the input failed (2), so the prefix is dropped.
        PythonNtRealPath.RealPath(@"real\missing\x.json", system).Should().Be(@"C:\Work\Real\missing\x.json");
        system.Opened.Should().Equal(
            @"C:\Work\real\missing\x.json",
            @"C:\Work\real\missing\x.json",
            @"C:\Work\real\missing",
            @"C:\Work\real",
            @"C:\Work\Real\missing\x.json");
    }

    [Fact]
    public void ADanglingLinkIsFollowedByHandAndARelativeTargetResolvesAgainstTheLink()
    {
        var system = new FakeSystem();
        system.Links[@"C:\WORK\LINK"] = @"..\target\dir";
        system.Symlinks.Add(@"C:\WORK\LINK");
        system.Links[@"C:\TARGET\DIR"] = @"D:\elsewhere";

        PythonNtRealPath.RealPath(@"link\file.txt", system).Should().Be(@"D:\elsewhere\file.txt");
    }

    [Fact]
    public void ARelativeTargetOfSomethingThatIsNotASymlinkStopsTheChain()
    {
        var system = new FakeSystem();
        system.Links[@"C:\WORK\JUNCTION"] = "relative";

        PythonNtRealPath.RealPath(@"junction\file.txt", system).Should().Be(@"C:\Work\junction\file.txt");
    }

    [Fact]
    public void AnAccessRefusalReadsTheStoredNameWithoutOpening()
    {
        var system = new FakeSystem();
        system.Errors[@"C:\PAGEFILE.SYS"] = 5;
        system.StoredNames[@"C:\PAGEFILE.SYS"] = "pagefile.sys";
        system.Final[@"C:\"] = @"\\?\C:\";

        PythonNtRealPath.RealPath(@"C:\PAGEFILE.SYS", system).Should().Be(@"C:\pagefile.sys");
    }

    [Fact]
    public void AnUnexpectedErrorEscapes()
    {
        var system = new FakeSystem();
        system.Errors[@"C:\X"] = 1450;

        var act = () => PythonNtRealPath.RealPath(@"C:\x", system);
        act.Should().Throw<NtPathException>().Which.WinError.Should().Be(1450);
    }

    [Fact]
    public void NulAndEmbeddedNulAreSpecialCased()
    {
        var system = new FakeSystem();
        PythonNtRealPath.RealPath("NUL", system).Should().Be(@"\\.\NUL");
        PythonNtRealPath.RealPath("a\0b", system).Should().Be("C:\\Work\\a\0b");
        system.Opened.Should().ContainSingle();
    }

    [Fact]
    public void AUncRootKeepsItsSeparatorAndARootedPathTakesTheWorkingDrive()
    {
        var system = new FakeSystem();
        system.Final[@"\\SERVER\SHARE\"] = @"\\?\UNC\server\share\";

        PythonNtRealPath.RealPath(@"//server/share/", system).Should().Be(@"\\server\share\");
        PythonNtRealPath.RealPath(@"\rooted\x", system).Should().Be(@"C:\rooted\x", "ntpath.isabs is false for a rooted path without a drive, so it is joined onto the working directory");
    }
}
