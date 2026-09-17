using System.Globalization;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

/// <summary><c>subprocess.CalledProcessError</c>: a command run with <c>check=True</c> exited with a non-zero status.</summary>
internal sealed class CalledProcessException : Exception
{
    public CalledProcessException()
    {
    }

    public CalledProcessException(string message)
        : base(message)
    {
    }

    public CalledProcessException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public CalledProcessException(IReadOnlyList<string> command, int exitCode)
        : base(string.Create(
            CultureInfo.InvariantCulture,
            $"Command '{PythonRepr.Repr(command.Cast<object?>().ToList())}' returned non-zero exit status {exitCode}."))
    {
    }
}
