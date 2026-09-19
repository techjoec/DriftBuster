namespace DriftBuster.Backend.Remote;

public sealed record CaptureCountDelta(int Baseline, int Current, int Delta);
