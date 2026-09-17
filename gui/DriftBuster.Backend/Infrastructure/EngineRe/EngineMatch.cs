namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary><c>re.Match</c> over a str subject; offsets are UTF-16 indices (see <see cref="EnginePattern"/>).</summary>
public sealed class EngineMatch
{
    private readonly EnginePattern _pattern;
    private readonly string _text;
    private readonly int[] _spans;

    internal EngineMatch(EnginePattern pattern, string text, int[] spans, int? lastIndex)
    {
        _pattern = pattern;
        _text = text;
        _spans = spans;
        LastIndex = lastIndex;
    }

    /// <summary><c>match.start()</c>.</summary>
    public int Start => _spans[0];

    /// <summary><c>match.end()</c>.</summary>
    public int End => _spans[1];

    /// <summary><c>match.group(0)</c>.</summary>
    public string Value => _text[_spans[0].._spans[1]];

    /// <summary><c>match.lastindex</c>.</summary>
    public int? LastIndex { get; }

    /// <summary><c>match.group(index)</c>: null for a group that did not participate.</summary>
    public string? Group(int index)
    {
        if (index < 0 || index > _pattern.Groups)
        {
            throw new EngineIndexException(nameof(index), "no such group");
        }

        var start = _spans[index * 2];
        return start < 0 ? null : _text[start.._spans[(index * 2) + 1]];
    }

    /// <summary><c>match.start(index)</c>, or -1 for a group that did not participate.</summary>
    public int GroupStart(int index)
    {
        if (index < 0 || index > _pattern.Groups)
        {
            throw new EngineIndexException(nameof(index), "no such group");
        }

        return _spans[index * 2];
    }
}
