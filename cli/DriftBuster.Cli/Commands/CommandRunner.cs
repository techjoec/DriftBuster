using System.CommandLine;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// Runs a command body with the invocation's stdout and stderr. A <see cref="CommandExitException"/> writes the message on
/// stderr and exits 1; any other exception ends the command with exit code 1 and <c>ExceptionTypeName: message</c> on stderr.
/// </summary>
internal static class CommandRunner
{
    public static int Run(ParseResult parseResult, Func<TextWriter, TextWriter, int> body)
    {
        var stdout = parseResult.InvocationConfiguration.Output;
        var stderr = parseResult.InvocationConfiguration.Error;
        try
        {
            return body(stdout, stderr);
        }
        catch (CommandExitException exc)
        {
            ConsoleText.Print(stderr, exc.Message);
            return 1;
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            ConsoleText.Print(stderr, $"{exc.GetType().Name}: {ErrorText.Plain(exc)}");
            return 1;
        }
    }

    /// <summary><c>parser.error(message)</c>: <c>{prog}: error: {message}</c> on stderr and exit code 2 (the usage line is not repeated).</summary>
    public static int ParserError(TextWriter stderr, string prog, string message)
    {
        ConsoleText.Print(stderr, $"{prog}: error: {message}");
        return 2;
    }
}
