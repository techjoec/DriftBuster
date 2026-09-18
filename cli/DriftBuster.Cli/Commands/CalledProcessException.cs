using System.Globalization;

namespace DriftBuster.Cli.Commands;

/// <summary>A command the tool ran exited with a non-zero status.</summary>
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
            $"The command '{string.Join(' ', command)}' exited with code {exitCode}."))
    {
    }
}
