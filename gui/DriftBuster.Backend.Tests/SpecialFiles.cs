using System.Diagnostics;
using System.Net.Sockets;

namespace DriftBuster.Backend.Tests;

/// <summary>
/// Linux directory entries that are not regular files (a FIFO, a Unix socket, a link to a character device) and a file whose
/// name is not valid UTF-8. Opening the FIFO for reading blocks until a writer appears, so a walk that opens it never ends.
/// </summary>
internal sealed class SpecialFiles : IDisposable
{
    private readonly Socket _socket;

    private SpecialFiles(string directory)
    {
        Directory.CreateDirectory(directory);
        Fifo = Path.Combine(directory, "pipe.ini");
        Socket = Path.Combine(directory, "socket.ini");
        Device = Path.Combine(directory, "device.ini");
        Run("mkfifo", Fifo);
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket.Bind(new UnixDomainSocketEndPoint(Socket));
        File.CreateSymbolicLink(Device, "/dev/null");
    }

    public string Fifo { get; }

    public string Socket { get; }

    public string Device { get; }

    public IEnumerable<string> All => [Fifo, Socket, Device];

    /// <summary>Creates the three entries under <paramref name="directory"/>; Linux only.</summary>
    public static SpecialFiles Create(string directory) => new(directory);

    /// <summary>Creates <c>&lt;directory&gt;/bad-\xff.ini</c> (a lone 0xFF byte in the name) holding <paramref name="content"/>.</summary>
    public static void CreateUndecodableName(string directory, string content)
        => Run("sh", "-c", "printf '%s' \"$2\" > \"$1/bad-$(printf '\\377').ini\"", "sh", directory, content);

    /// <summary>Creates <c>&lt;directory&gt;/d\xff/child.ini</c> (a lone 0xFF byte in the directory name) holding <paramref name="content"/>.</summary>
    public static void CreateUndecodableDirectory(string directory, string content)
        => Run("sh", "-c", "d=\"$1/d$(printf '\\377')\"; mkdir \"$d\" && printf '%s' \"$2\" > \"$d/child.ini\"", "sh", directory, content);

    /// <summary>
    /// Runs <paramref name="script"/> under <c>sh -c</c> with <paramref name="arguments"/> as <c>$1</c>...; <c>$ff</c> holds the
    /// single byte 0xFF and <c>$fffd</c> the UTF-8 bytes of U+FFFD, for names and link targets the runtime cannot spell.
    /// </summary>
    public static void Shell(string script, params string[] arguments)
        => Run("sh", ["-c", $"ff=\"$(printf '\\377')\"; fffd=\"$(printf '\\357\\277\\275')\"; {script}", "sh", .. arguments]);

    public void Dispose() => _socket.Dispose();

    /// <summary>
    /// Deletes a test's own temporary tree; <see cref="Directory.Delete(string, bool)"/> cannot remove an entry whose name it
    /// cannot decode, so on Unix the tree goes through <c>rm</c>.
    /// </summary>
    public static void DeleteTree(DirectoryInfo directory)
    {
        if (OperatingSystem.IsWindows())
        {
            directory.Delete(recursive: true);
            return;
        }

        // A test may leave a directory that cannot be searched; restore the owner's permissions first (best effort).
        using (var chmod = Process.Start(new ProcessStartInfo("chmod", ["-R", "u+rwX", "--", directory.FullName]) { RedirectStandardError = true, UseShellExecute = false })!)
        {
            chmod.StandardError.ReadToEnd();
            chmod.WaitForExit();
        }

        Run("rm", "-rf", "--", directory.FullName);
    }

    /// <summary>
    /// Creates <c>&lt;directory&gt;/<paramref name="name"/>/x.conf</c> holding <paramref name="content"/> and makes the
    /// subdirectory readable but not searchable (mode 0644): it can be listed, but nothing inside it can be looked up.
    /// Returns the file's path.
    /// </summary>
    public static string CreateUnsearchableDirectory(string directory, string name, string content)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("search permission on a directory is a Unix case");
        }

        var sub = Directory.CreateDirectory(Path.Combine(directory, name));
        var file = Path.Combine(sub.FullName, "x.conf");
        File.WriteAllText(file, content);
        File.SetUnixFileMode(sub.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return file;
    }

    private static void Run(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, error);
    }
}
