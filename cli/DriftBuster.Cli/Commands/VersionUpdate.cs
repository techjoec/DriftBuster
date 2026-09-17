namespace DriftBuster.Cli.Commands;

/// <summary>One <c>update_file(path, pattern, replacement, count=count)</c> call; a count of zero replaces every match.</summary>
internal sealed record VersionUpdate(string Path, string Pattern, string Replacement, int Count = 0);
