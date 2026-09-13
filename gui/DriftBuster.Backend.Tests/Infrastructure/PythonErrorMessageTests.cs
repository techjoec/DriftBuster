using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// Exceptions that mirror a Python <c>ValueError</c> or <c>IndexError</c> carry CPython 3.13's message text exactly, with no
/// " (Parameter '...')" suffix, and still name the argument.
/// </summary>
public sealed class PythonErrorMessageTests
{
    public static TheoryData<string, string, string> Cases() => new()
    {
        { "placeholder", "placeholder_template must include {token_name} placeholder", "template" },
        { "glob", "Unacceptable pattern: PosixPath('.')", "pattern" },
        { "purepath", "empty pattern", "pattern" },
        { "locale", "cannot use LOCALE flag with a str pattern", "flags" },
        { "ascii", "ASCII and UNICODE flags are incompatible", "flags" },
        { "group", "no such group", "index" },
        { "start", "no such group", "index" },
        { "results", "results must not be empty", "results" },
        { "baseline", "baseline_names length must match results", "baselineNames" },
        { "comparison", "comparison_names length must match results", "comparisonNames" },
        { "redactor", "Provide either an explicit redactor or mask_tokens, not both.", "maskTokens" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MessagesAreCPythonsText(string site, string message, string parameter)
    {
        var artifact = DiffBuilder.BuildUnifiedDiff("a\n", "b\n", "text");
        Action action = site switch
        {
            "placeholder" => () => HuntEngine.FormatPlaceholder("{other}", "name"),
            "glob" => () => PythonGlob.Glob(".", string.Empty),
            "purepath" => () => PythonPurePath.Match("a", string.Empty),
            "locale" => () => PythonPattern.Compile("a", PythonReFlags.Locale),
            "ascii" => () => PythonPattern.Compile("a", PythonReFlags.Ascii | PythonReFlags.Unicode),
            "group" => () => PythonPattern.Compile("a").Match("a")!.Group(5),
            "start" => () => PythonPattern.Compile("a").Match("a")!.GroupStart(-1),
            "results" => () => DiffBuilder.SummariseDiffResults([]),
            "baseline" => () => DiffBuilder.SummariseDiffResults([artifact], baselineNames: ["x", "y"]),
            "comparison" => () => DiffBuilder.SummariseDiffResults([artifact], comparisonNames: ["x", "y"]),
            _ => () => RedactionFilter.Resolve(new RedactionFilter(["x"]), ["y"]),
        };

        var thrown = action.Should().Throw<ArgumentException>().Which;
        thrown.Message.Should().Be(message);
        thrown.ParamName.Should().Be(parameter);
    }
}
