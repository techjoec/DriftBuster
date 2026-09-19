namespace DriftBuster.Backend.Remote;

/// <summary>What identifies a detection across captures: its file (relative path, else full path), format and variant.</summary>
public sealed record CaptureDetectionKey(string Location, string Format, string? Variant)
{
    public override string ToString() => $"{Location} ({Format}{(Variant is null ? string.Empty : "/" + Variant)})";
}
