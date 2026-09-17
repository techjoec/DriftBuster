using System.CommandLine;
using System.Text;

using DriftBuster.Cli.Commands;

namespace DriftBuster.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var stdin = OpenStandardInput();
        return Run(args, Console.Out, Console.Error, stdin);
    }

    public static RootCommand BuildRootCommand() => BuildRootCommand(() => Console.In);

    /// <summary>
    /// Standard input decoded as UTF-8 on every platform (a byte order mark is skipped): <see cref="Console.In"/> decodes redirected input
    /// with the console input code page on Windows.
    /// </summary>
    internal static TextReader OpenStandardInput()
        => new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);

    /// <summary>The <c>driftbuster</c> commands, with <paramref name="stdin"/> as the stream <c>multi-server</c> reads its request from.</summary>
    public static RootCommand BuildRootCommand(Func<TextReader> stdin)
    {
        var root = new RootCommand("DriftBuster configuration drift tooling.");
        root.Subcommands.Add(ScanCommand.Build());
        root.Subcommands.Add(DiffCommand.Build());
        root.Subcommands.Add(HuntCommand.Build());
        root.Subcommands.Add(MultiServerCommand.Build(stdin));
        root.Subcommands.Add(ProfileCommand.Build());
        root.Subcommands.Add(DetectionProfileCommand.Build());
        root.Subcommands.Add(ScheduleCommand.Build());
        root.Subcommands.Add(RegistryScanCommand.Build());
        root.Subcommands.Add(SqlExportCommand.Build());
        root.Subcommands.Add(ReportCommand.Build());
        root.Subcommands.Add(CaptureCommand.Build());
        root.Subcommands.Add(ParityDump.Build());
        return root;
    }

    /// <summary>
    /// Parses and runs <paramref name="args"/> with text-mode <paramref name="stdout"/> and <paramref name="stderr"/>. Response files are not
    /// expanded (an argument starting with "@" is a value). A parse error writes each error on stderr and exits 2, as <c>argparse</c>
    /// exits; the wording and help text are System.CommandLine's.
    /// </summary>
    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, TextReader? stdin = null)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        using var output = new TextModeWriter(stdout);
        using var error = new TextModeWriter(stderr);
        var parseResult = BuildRootCommand(() => stdin ?? Console.In).Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
        try
        {
            if (parseResult.Errors.Count > 0)
            {
                foreach (var parseError in parseResult.Errors)
                {
                    error.Write($"driftbuster: error: {parseError.Message}\n");
                }

                error.Write("Run 'driftbuster --help' for usage.\n");
                return 2;
            }

            return parseResult.Invoke(new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false });
        }
        finally
        {
            output.Flush();
            error.Flush();
        }
    }
}
