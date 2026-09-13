namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary><c>re._parser.State</c>.</summary>
internal sealed class ReParseState
{
    /// <summary><c>_sre.MAXGROUPS</c> on 64-bit CPython.</summary>
    public const int MaxGroups = 1073741823;

    public PythonReFlags Flags { get; set; }

    public Dictionary<string, int> GroupDict { get; } = new(StringComparer.Ordinal);

    /// <summary>Per group number, its width once closed; slot 0 stands for the whole match.</summary>
    public List<(UInt128 Low, UInt128 High)?> GroupWidths { get; } = [null];

    public int? LookbehindGroups { get; set; }

    public Dictionary<int, int> GroupRefPositions { get; } = [];

    public int Groups => GroupWidths.Count;

    public int OpenGroup(string? name)
    {
        var gid = Groups;
        GroupWidths.Add(null);
        if (Groups > MaxGroups)
        {
            throw new PythonReException("too many groups");
        }

        if (name is not null)
        {
            if (GroupDict.TryGetValue(name, out var existing))
            {
                throw new PythonReException(
                    $"redefinition of group name {PythonRepr.StrRepr(name)} as group {gid}; was group {existing}");
            }

            GroupDict[name] = gid;
        }

        return gid;
    }

    public void CloseGroup(int gid, ReSubPattern body) => GroupWidths[gid] = body.GetWidth();

    public bool CheckGroup(int gid) => gid < Groups && GroupWidths[gid] is not null;

    public void CheckLookbehindGroup(int gid, ReTokenizer source)
    {
        if (LookbehindGroups is not { } lookbehindGroups)
        {
            return;
        }

        if (!CheckGroup(gid))
        {
            throw source.Error("cannot refer to an open group");
        }

        if (gid >= lookbehindGroups)
        {
            throw source.Error("cannot refer to group defined in the same lookbehind subpattern");
        }
    }
}
