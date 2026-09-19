namespace DriftBuster.Backend.Remote;

public sealed record CaptureRedaction(string Placeholder, int MaskTokenCount, long TotalRedactions);
