using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>Python's <c>OSError</c> texts that differ by platform, for tests that run on every host.</summary>
internal static class OSErrorTexts
{
    /// <summary><c>open()</c> on a directory: <c>[Errno 21] Is a directory</c> on Unix, <c>[Errno 13] Permission denied</c> on Windows.</summary>
    public static string DirectoryOpen(string path)
        => OperatingSystem.IsWindows()
            ? $"[Errno 13] Permission denied: {PythonRepr.StrRepr(path)}"
            : $"[Errno 21] Is a directory: {PythonRepr.StrRepr(path)}";

    /// <summary>The <c>OSError</c> subclass <see cref="DirectoryOpen"/> is raised as.</summary>
    public static string DirectoryOpenType => OperatingSystem.IsWindows() ? "PermissionError" : "IsADirectoryError";
}
