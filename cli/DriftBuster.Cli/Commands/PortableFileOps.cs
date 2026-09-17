namespace DriftBuster.Cli.Commands;

/// <summary>The file operations staging goes through: <c>shutil.rmtree</c> and <c>shutil.copy2</c>.</summary>
internal sealed record PortableFileOps(Action<string> DeleteTree, Action<string, string> CopyFile)
{
    public static PortableFileOps Default { get; } = new(path => Directory.Delete(path, recursive: true), CopyWithTimes);

    /// <summary><c>shutil.copy2(source, destination)</c>: the content, replacing the destination, with the modification time kept.</summary>
    public static void CopyWithTimes(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }
}
