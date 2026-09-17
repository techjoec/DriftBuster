namespace DriftBuster.Cli.Commands;

/// <summary>A path eligible for purging and its age in days.</summary>
internal sealed record PurgeCandidate(string Path, double AgeDays);
