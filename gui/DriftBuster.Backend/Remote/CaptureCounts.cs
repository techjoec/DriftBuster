namespace DriftBuster.Backend.Remote;

public sealed record CaptureCounts(int Detections, int ProfileMatches, int HuntHits, int RegistryScans, int Profiles, int ProfileConfigs);
