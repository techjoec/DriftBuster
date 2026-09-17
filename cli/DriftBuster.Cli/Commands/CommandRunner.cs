using System.CommandLine;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.EngineRe;
using DriftBuster.Backend.Scheduling;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// Runs a command body with the invocation's stdout and stderr. A <see cref="CommandExitException"/> writes the message on
/// stderr and exits 1; any other exception ends the command with exit code 1 and <c>TypeName: message</c> on stderr.
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
            ConsoleText.Print(stderr, $"{ErrorName(exc)}: {exc.Message}");
            return 1;
        }
    }

    /// <summary><c>parser.error(message)</c>: <c>{prog}: error: {message}</c> on stderr and exit code 2 (the usage line is not repeated).</summary>
    public static int ParserError(TextWriter stderr, string prog, string message)
    {
        ConsoleText.Print(stderr, $"{prog}: error: {message}");
        return 2;
    }

    /// <summary><c>type(exc).__name__</c> of the error kind an exception stands for; any other keeps its runtime name.</summary>
    public static string ErrorName(Exception exc) => exc switch
    {
        CommandExitException => "SystemExit",
        CalledProcessException => "subprocess.CalledProcessError",
        ScheduleException => "ScheduleError",
        Sqlite3Exception sqlite => sqlite.TypeName,
        EngineReException => "PatternError",
        DetectorIOException => "DetectorIOError",
        MetadataValidationError => "MetadataValidationError",
        EngineValueException => "ValueError",
        EngineRecursionException => "RecursionError",
        EngineTypeException => "TypeError",
        EngineIndexException => "IndexError",
        EngineAttributeException => "AttributeError",
        EngineUnicodeDecodeException => "UnicodeDecodeError",
        EngineNotImplementedException => "NotImplementedError",
        EngineRuntimeException => "RuntimeError",
        KeyNotFoundException => "KeyError",
        FileNotFoundException => "FileNotFoundError",
        OverflowException => "OverflowError",
        IOException { HResult: > 0 and < 4096 } => EngineOSError.TypeName(exc.HResult),
        UnauthorizedAccessException => "PermissionError",
        IOException => "OSError",
        _ => exc.GetType().Name,
    };
}
