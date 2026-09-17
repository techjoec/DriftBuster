using System.Globalization;
using System.Text.Json;

namespace DriftBuster.Cli.Tests;

/// <summary>One in-process run of <c>driftbuster</c> through <see cref="Program.Run"/>: exit code and the captured stdout and stderr.</summary>
internal sealed record CliInvocation(int ExitCode, string Out, string Err)
{
    public static CliInvocation Invoke(params string[] args) => InvokeWithInput(null, args);

    public static CliInvocation InvokeWithInput(string? stdin, params string[] args)
    {
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        using var input = stdin is null ? null : new StringReader(stdin);
        var code = Program.Run(args, stdout, stderr, input ?? TextReader.Null);
        return new CliInvocation(code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>stdout's non-empty lines, each parsed as JSON.</summary>
    public IReadOnlyList<JsonElement> JsonLines()
        => Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();

    /// <summary>stdout parsed as one JSON document.</summary>
    public JsonElement Json() => JsonDocument.Parse(Out).RootElement.Clone();
}
