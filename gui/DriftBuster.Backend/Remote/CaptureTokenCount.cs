namespace DriftBuster.Backend.Remote;

public sealed record CaptureTokenCount(string Token, int Baseline, int Current, int Delta);
