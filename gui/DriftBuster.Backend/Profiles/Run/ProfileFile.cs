using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>A collected file: the source it came from, where it was copied, and the copy's size and SHA-256.</summary>
public sealed record ProfileFile(string Source, string Destination, long Size, string Sha256)
{
    /// <summary>The record written to <c>metadata.json</c>, destination in posix form.</summary>
    public OrderedDictionary<string, object?> ToDict() => new(StringComparer.Ordinal)
    {
        ["source"] = Source,
        ["destination"] = PathText.ToPosix(Destination),
        ["size"] = Size,
        ["sha256"] = Sha256,
    };
}
