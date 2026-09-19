using System.CommandLine;
using System.CommandLine.Parsing;

namespace DriftBuster.Cli.Commands;

/// <summary>Option and argument builders; a value an option cannot hold is a parse error (exit code 2).</summary>
internal static class CliOptions
{
    public static Option<string> Text(string name, string defaultValue, string description)
        => new(name) { DefaultValueFactory = _ => defaultValue, Description = description };

    public static Option<string?> OptionalText(string name, string description) => new(name) { Description = description };

    public static Option<bool> Flag(string name, string description) => new(name) { Description = description };

    /// <summary>Every occurrence adds one value.</summary>
    public static Option<string[]> Append(string name, string description)
        => new(name) { DefaultValueFactory = _ => [], Description = description, Arity = ArgumentArity.OneOrMore, AllowMultipleArgumentsPerToken = false };

    /// <summary>An integer without a default: null when absent.</summary>
    public static Option<int?> OptionalInt(string name, string description) => new(name) { Description = description };

    /// <summary>An integer with a default.</summary>
    public static Option<int> Int(string name, int defaultValue, string description)
        => new(name) { Description = description, DefaultValueFactory = _ => defaultValue };

    /// <summary>A number with a default.</summary>
    public static Option<double> Float(string name, double defaultValue, string description)
        => new(name) { Description = description, DefaultValueFactory = _ => defaultValue };

    /// <summary>A positional value; one that starts with "-" is an unknown option, not a path.</summary>
    public static Argument<string> Positional(string name, string description)
    {
        var argument = new Argument<string>(name) { Description = description };
        argument.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && result.Tokens[0].Value is { Length: > 1 } value && value[0] == '-')
            {
                result.AddError($"unrecognized arguments: {value}");
            }
        });
        return argument;
    }
}
