using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <see cref="PythonNtPath"/> and the Windows flavour of <see cref="PythonPurePath"/> against CPython 3.13 <c>ntpath</c> and
/// <c>PureWindowsPath</c> on <c>Data/python_ntpath_cases.json</c> (regenerate with <c>tools/parity/gen_ntpath_cases.py</c>): UNC and
/// <c>\\?\</c> anchors with the separator after the share as the root, drive-relative paths, surrogate-pair drive letters,
/// <c>normpath</c>, <c>split</c>, <c>join</c>, <c>relative_to</c> (case-folded) and <c>match</c> (case-insensitive). Neither touches a
/// file system, so the comparison runs on every host.
/// </summary>
public sealed class PythonNtPathTests
{
    private static readonly Lazy<OrderedDictionary<string, object?>> Data = new(() =>
    {
        var path = Path.Combine(RepoPaths.Root, "gui", "DriftBuster.Backend.Tests", "Infrastructure", "Data", "python_ntpath_cases.json");
        PythonJson.TryLoads(File.ReadAllText(path), out var value).Should().BeTrue();
        return (OrderedDictionary<string, object?>)value!;
    });

    private static IEnumerable<OrderedDictionary<string, object?>> Section(string name)
        => ((List<object?>)Data.Value[name]!).Cast<OrderedDictionary<string, object?>>();

    private static IEnumerable<string> Strings(object? value) => ((List<object?>)value!).Cast<string>();

    [Fact]
    public void SplitRootSplitDriveIsAbsNormPathAndSplitAgreeWithNtPath()
    {
        foreach (var entry in Section("ntpath"))
        {
            var path = (string)entry["path"]!;
            var because = PythonRepr.StrRepr(path);
            var (drive, root, remainder) = PythonNtPath.SplitRoot(path);
            new[] { drive, root, remainder }.Should().Equal(Strings(entry["splitroot"]), because);
            var (splitDrive, rest) = PythonNtPath.SplitDrive(path);
            new[] { splitDrive, rest }.Should().Equal(Strings(entry["splitdrive"]), because);
            PythonNtPath.IsAbs(path).Should().Be((bool)entry["isabs"]!, because);
            PythonNtPath.NormPath(path).Should().Be((string)entry["normpath"]!, because);
            var (head, tail) = PythonNtPath.Split(path);
            new[] { head, tail }.Should().Equal(Strings(entry["split"]), because);
        }
    }

    [Fact]
    public void JoinAgreesWithNtPath()
    {
        foreach (var entry in Section("join"))
        {
            var path = (string)entry["path"]!;
            var other = (string)entry["other"]!;
            PythonNtPath.Join(path, other).Should().Be((string)entry["result"]!, $"ntpath.join({PythonRepr.StrRepr(path)}, {PythonRepr.StrRepr(other)})");
        }
    }

    [Fact]
    public void PartsStrParentAnchorIsAbsoluteRelativeToAndJoinAgreeWithPureWindowsPath()
    {
        foreach (var entry in Section("pure_windows"))
        {
            var path = (string)entry["path"]!;
            var other = (string)entry["other"]!;
            var because = $"PureWindowsPath({PythonRepr.StrRepr(path)}), other {PythonRepr.StrRepr(other)}";
            PythonPurePath.Parts(path, windows: true).Should().Equal(Strings(entry["parts"]), because);
            PythonPurePath.Str(path, windows: true).Should().Be((string)entry["str"]!, because);
            PythonPurePath.Parent(path, windows: true).Should().Be((string)entry["parent"]!, because);
            PythonPurePath.Parse(path, windows: true).Anchor.Should().Be((string)entry["anchor"]!, because);
            PythonPurePath.IsAbsolute(path, windows: true).Should().Be((bool)entry["is_absolute"]!, because);
            PythonPurePath.RelativeTo(path, other, windows: true).Should().Be((string?)entry["relative_to"], because);
            PythonPurePath.Join(path, other, windows: true).Should().Be((string)entry["joined"]!, because);
        }
    }

    [Fact]
    public void MatchAgreesWithPureWindowsPath()
    {
        foreach (var entry in Section("match"))
        {
            var path = (string)entry["path"]!;
            var pattern = (string)entry["pattern"]!;
            var match = () => PythonPurePath.Match(path, pattern, windows: true);
            if (entry.TryGetValue("error", out var error))
            {
                match.Should().Throw<ArgumentException>().Which.Message.Should().StartWith((string)error!);
                continue;
            }

            match().Should().Be((bool)entry["result"]!, $"PureWindowsPath({PythonRepr.StrRepr(path)}).match({PythonRepr.StrRepr(pattern)})");
        }
    }

    [Fact]
    public void AbsoluteInTheWindowsFlavourFollowsPathAbsolute()
    {
        static string DriveCwd(string drive) => drive + @"\drive-cwd";
        PythonPath.Absolute(@"\\server\share\x", windows: true, @"C:\Work", DriveCwd).Should().Be(@"\\server\share\x");
        PythonPath.Absolute(@"\rooted\x", windows: true, @"C:\Work", DriveCwd).Should().Be(@"C:\rooted\x");
        PythonPath.Absolute(@"D:x\y", windows: true, @"C:\Work", DriveCwd).Should().Be(@"D:\drive-cwd\x\y");
        PythonPath.Absolute(@"D:", windows: true, @"C:\Work", DriveCwd).Should().Be(@"D:\drive-cwd");
        PythonPath.Absolute(@"a\..\b", windows: true, @"C:\Work", DriveCwd).Should().Be(@"C:\Work\a\..\b");
        PythonPath.Absolute("", windows: true, @"C:\Work", DriveCwd).Should().Be(@"C:\Work");
        PythonPath.Absolute("x", windows: true, @"\\server\share\", DriveCwd).Should().Be(@"\\server\share\x");
        PythonPath.Absolute("x", windows: true, @"\\server\share\sub", DriveCwd).Should().Be(@"\\server\share\sub\x");
    }
}
