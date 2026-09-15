using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary><c>run_profiles.ProfileFile</c>: the source string a file came from, where it was copied, and the copy's size and SHA-256.</summary>
public sealed record ProfileFile(string Source, string Destination, long Size, string Sha256)
{
    /// <summary>The record <c>to_dict</c> and <c>metadata.json</c> write: the destination in posix form.</summary>
    public OrderedDictionary<string, object?> ToDict() => new(StringComparer.Ordinal)
    {
        ["source"] = Source,
        ["destination"] = PathText.ToPosix(Destination),
        ["size"] = Size,
        ["sha256"] = Sha256,
    };
}
