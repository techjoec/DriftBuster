using System.CommandLine;
using System.CommandLine.Parsing;
using System.Numerics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// Options and arguments spelled and converted as <c>argparse</c> declares them: <c>type=int</c> is <c>int()</c> of any size,
/// <c>type=float</c> is <c>float()</c>, <c>action="append"</c> repeats the option once per value. A value the conversion refuses is a
/// parse error (exit code 2), as <c>argparse</c> reports it.
/// </summary>
internal static class EngineArguments
{
    public static Option<string> Text(string name, string defaultValue, string description)
        => new(name) { DefaultValueFactory = _ => defaultValue, Description = description };

    public static Option<string?> OptionalText(string name, string description) => new(name) { Description = description };

    public static Option<bool> Flag(string name, string description) => new(name) { Description = description };

    /// <summary><c>action="append"</c>: every occurrence adds one value.</summary>
    public static Option<string[]> Append(string name, string description)
        => new(name) { DefaultValueFactory = _ => [], Description = description, Arity = ArgumentArity.OneOrMore, AllowMultipleArgumentsPerToken = false };

    /// <summary><c>type=int</c> without a default: null when absent.</summary>
    public static Option<BigInteger?> OptionalInt(string name, string description)
        => new(name) { Description = description, CustomParser = result => ParseInt(result, name) };

    /// <summary><c>type=int</c> with a default.</summary>
    public static Option<BigInteger> Int(string name, BigInteger defaultValue, string description)
        => new(name) { Description = description, DefaultValueFactory = _ => defaultValue, CustomParser = result => ParseInt(result, name) ?? defaultValue };

    /// <summary><c>type=float</c> with a default.</summary>
    public static Option<double> Float(string name, double defaultValue, string description)
        => new(name) { Description = description, DefaultValueFactory = _ => defaultValue, CustomParser = result => ParseFloat(result, name) ?? defaultValue };

    /// <summary>A positional value; one that starts with "-" is an unknown option, not a path (as argparse reads it).</summary>
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

    private static BigInteger? ParseInt(ArgumentResult result, string name)
    {
        var text = result.Tokens.Count > 0 ? result.Tokens[^1].Value : null;
        if (text is null)
        {
            return null;
        }

        try
        {
            return EngineBuiltins.Int(text);
        }
        catch (FormatException exc)
        {
            result.AddError($"argument {name}: {exc.Message}");
            return null;
        }
    }

    private static double? ParseFloat(ArgumentResult result, string name)
    {
        var text = result.Tokens.Count > 0 ? result.Tokens[^1].Value : null;
        if (text is null)
        {
            return null;
        }

        try
        {
            return EngineBuiltins.Float(text);
        }
        catch (FormatException exc)
        {
            result.AddError($"argument {name}: {exc.Message}");
            return null;
        }
    }
}
