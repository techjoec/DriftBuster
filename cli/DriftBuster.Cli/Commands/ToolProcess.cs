using System.ComponentModel;
using System.Diagnostics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>run(command, cwd=cwd)</c> of the maintenance scripts: prints the command after a blank line and an arrow, then runs it with the
/// console inherited and <c>check=True</c>, so a non-zero exit raises <see cref="CalledProcessException"/>.
/// </summary>
internal static class ToolProcess
{
    /// <summary>Starts <c>command[0]</c> with the remaining items as arguments in <c>cwd</c> (the current directory when null) and returns its exit code.</summary>
    public static int Launch(IReadOnlyList<string> command, string? cwd)
    {
        ArgumentNullException.ThrowIfNull(command);
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? command[0] : SearchPath(command[0])) { UseShellExecute = false };
        foreach (var argument in command.Skip(1))
        {
            info.ArgumentList.Add(argument);
        }

        if (cwd is not null)
        {
            info.WorkingDirectory = cwd;
        }

        try
        {
            using var process = Process.Start(info) ?? throw EngineOSError.Create(EngineOSError.NoSuchFile, command[0]);
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception exc)
        {
            throw EngineOSError.Create(EngineOSError.NoSuchFile, command[0], exc);
        }
    }

    // execvp's lookup: a name without a slash is found in the PATH directories in order (an empty entry is the current directory),
    // where the runtime would look beside the running host first.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string SearchPath(string name)
    {
        if (name.Contains('/', StringComparison.Ordinal))
        {
            return name;
        }

        const UnixFileMode Executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "/bin:/usr/bin").Split(':');
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory.Length == 0 ? "." : directory, name);
            if (File.Exists(candidate) && (File.GetUnixFileMode(candidate) & Executable) != UnixFileMode.None)
            {
                return candidate;
            }
        }

        throw EngineOSError.Create(EngineOSError.NoSuchFile, name);
    }

    public static void Run(IReadOnlyList<string> command, string? cwd, string arrow, TextWriter stdout, Func<IReadOnlyList<string>, string?, int> launcher)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(launcher);
        ConsoleText.Print(stdout, $"\n{arrow} {string.Join(' ', command)}");
        stdout.Flush();
        var exitCode = launcher(command, cwd);
        if (exitCode != 0)
        {
            throw new CalledProcessException(command, exitCode);
        }
    }
}
