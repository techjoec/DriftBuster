namespace DriftBuster.Backend.Infrastructure;

/// <summary>Paths as people write them in configs and on the command line.</summary>
public static class PathExpansion
{
    /// <summary>A leading <c>~</c> (alone or before a separator) as the home directory, then <c>%VAR%</c> environment variables.</summary>
    public static string Expand(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var withHome = string.Equals(path, "~", StringComparison.Ordinal) ? home
            : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal) ? home + path[1..]
            : path;
        return Environment.ExpandEnvironmentVariables(withHome);
    }
}
