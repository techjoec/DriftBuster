namespace DriftBuster.Backend.Infrastructure
{
    /// <summary>
    /// Locates the repository checkout that contains a given directory by walking up to the
    /// solution file. Used by tests and tooling that resolve <c>fixtures/</c> and scripts.
    /// </summary>
    public static class RepositoryRoot
    {
        private const string Marker = "DriftBuster.sln";

        public static string? Find(string? startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
            {
                return null;
            }

            DirectoryInfo? current;
            try
            {
                current = new DirectoryInfo(startDirectory);
            }
            catch (ArgumentException)
            {
                return null;
            }

            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, Marker)))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        public static string Require()
        {
            return Find(AppContext.BaseDirectory)
                ?? Find(Environment.CurrentDirectory)
                ?? throw new DirectoryNotFoundException($"Could not locate {Marker} above {AppContext.BaseDirectory} or {Environment.CurrentDirectory}.");
        }
    }
}
