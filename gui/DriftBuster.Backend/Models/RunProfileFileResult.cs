namespace DriftBuster.Backend.Models;

/// <summary>A collected file: the source it came from, where it was copied (forward slashes), and the copy's size and SHA-256.</summary>
public sealed record RunProfileFileResult(string Source, string Destination, long Size, string Sha256);
