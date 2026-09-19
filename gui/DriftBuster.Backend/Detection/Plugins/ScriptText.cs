using System.Text.RegularExpressions;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Line-level reading of Windows script configuration: PowerShell, batch/CMD and VBScript. Each pattern runs on one trimmed
/// line, never across lines, so no input can make a search revisit text.
/// </summary>
/// <remarks>Derived from publicly documented behavior, not vendor source (the PowerShell, cmd.exe and VBScript language references).</remarks>
internal static partial class ScriptText
{
    internal enum Language
    {
        PowerShell,
        Batch,
        VbScript,
    }

    /// <summary>One variable assignment a reader would call a setting.</summary>
    internal readonly record struct Assignment(Language Language, string Name, string Value);

    // $Name = value, $env:NAME = value, [type]$Name = value (a param block default), $script:Name = value.
    [GeneratedRegex(@"^(?:\[[\w.\[\]]+\]\s*)*\$(?<name>(?:env:|script:|global:)?[A-Za-z_][\w]*)\s*=(?!=)\s*(?<value>.*?)[,)]?$", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex PowerShellAssignment();

    // A Verb-Noun command call or function name.
    [GeneratedRegex(@"\b(?:Get|Set|New|Remove|Invoke|Write|Read|Import|Export|Start|Stop|Test|Add|Out|Select|Where|ForEach|Join|Split|Copy|Move|Resolve|Register|Enable|Disable|Install|Update|ConvertTo|ConvertFrom)-[A-Z][A-Za-z]+\b", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex PowerShellCommand();

    // set NAME=value and set "NAME=value" (not set /a or set /p, which compute or prompt).
    [GeneratedRegex(@"^set\s+(?<quote>"")?(?<name>[^\s=/""][^=""]*)=(?<value>[^""]*)(?(quote)""|)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex BatchSet();

    // Const Name = value, and a plain Name = value or Name = "text" assignment.
    [GeneratedRegex(@"^(?:(?<const>(?:Public\s+|Private\s+)?Const)\s+)?(?<name>[A-Za-z][\w]*)\s*=\s*(?<value>.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex VbAssignment();

    /// <summary>The number of distinct structural signals of each language in the text.</summary>
    internal static IReadOnlyDictionary<Language, int> Signals(IEnumerable<string> lines)
    {
        var found = new Dictionary<Language, HashSet<string>>
        {
            [Language.PowerShell] = new(StringComparer.Ordinal),
            [Language.Batch] = new(StringComparer.Ordinal),
            [Language.VbScript] = new(StringComparer.Ordinal),
        };
        foreach (var line in lines)
        {
            AddPowerShellSignals(line, found[Language.PowerShell]);
            AddBatchSignals(line, found[Language.Batch]);
            AddVbSignals(line, found[Language.VbScript]);
        }

        return found.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
    }

    /// <summary>The variable assignments in the text, in file order, for the given language.</summary>
    internal static IEnumerable<Assignment> Assignments(IEnumerable<string> lines, Language language)
    {
        foreach (var line in lines)
        {
            if (TryAssignment(line, language, out var assignment))
            {
                yield return assignment;
            }
        }
    }

    /// <summary>Trimmed lines with universal newlines.</summary>
    internal static IEnumerable<string> Lines(string text) => text.Split('\n').Select(line => line.TrimEnd('\r').Trim());

    private static bool TryAssignment(string line, Language language, out Assignment assignment)
    {
        assignment = default;
        switch (language)
        {
            case Language.PowerShell:
                var ps = PowerShellAssignment().Match(line);
                if (ps.Success)
                {
                    assignment = new Assignment(language, "$" + ps.Groups["name"].Value, Unquote(ps.Groups["value"].Value.Trim()));
                    return true;
                }

                return false;
            case Language.Batch:
                var set = BatchSet().Match(line);
                if (set.Success)
                {
                    assignment = new Assignment(language, set.Groups["name"].Value.Trim(), set.Groups["value"].Value.Trim());
                    return true;
                }

                return false;
            default:
                if (line.StartsWith('\'') || line.StartsWith("Set ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Dim ", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var vb = VbAssignment().Match(line);
                if (vb.Success && !vb.Groups["value"].Value.StartsWith('='))
                {
                    var name = vb.Groups["const"].Success ? "Const " + vb.Groups["name"].Value : vb.Groups["name"].Value;
                    assignment = new Assignment(language, name, Unquote(StripVbComment(vb.Groups["value"].Value).Trim()));
                    return true;
                }

                return false;
        }
    }

    private static void AddPowerShellSignals(string line, HashSet<string> signals)
    {
        if (line.StartsWith("param(", StringComparison.OrdinalIgnoreCase) || line.StartsWith("param (", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("param block");
        }

        if (line.StartsWith("#Requires", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("#Requires");
        }

        if (line.StartsWith("function ", StringComparison.OrdinalIgnoreCase) && PowerShellCommand().IsMatch(line))
        {
            signals.Add("Verb-Noun function");
        }
        else if (PowerShellCommand().IsMatch(line))
        {
            signals.Add("Verb-Noun command");
        }

        if (PowerShellAssignment().IsMatch(line))
        {
            signals.Add(line.StartsWith("$env:", StringComparison.OrdinalIgnoreCase) ? "$env: assignment" : "$variable assignment");
        }
    }

    private static void AddBatchSignals(string line, HashSet<string> signals)
    {
        if (line.StartsWith("@echo off", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("@echo off");
        }

        if (BatchSet().IsMatch(line))
        {
            signals.Add("set NAME=value");
        }

        if (line.StartsWith("goto ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("call ", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("goto/call");
        }

        if (line.StartsWith("exit /b", StringComparison.OrdinalIgnoreCase) || line.StartsWith("setlocal", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("exit /b or setlocal");
        }

        if (line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("::", StringComparison.Ordinal))
        {
            signals.Add("rem comment");
        }
    }

    private static void AddVbSignals(string line, HashSet<string> signals)
    {
        if (line.StartsWith("Option Explicit", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("Option Explicit");
        }

        if (line.StartsWith("Dim ", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("Dim");
        }

        if (line.Contains("CreateObject(", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("CreateObject");
        }

        if (line.Contains("WScript.", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("WScript");
        }

        if (line.StartsWith("End Sub", StringComparison.OrdinalIgnoreCase) || line.StartsWith("End Function", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("End Sub/Function");
        }
    }

    // A trailing ' comment outside a string literal.
    private static string StripVbComment(string value)
    {
        var inString = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '"')
            {
                inString = !inString;
            }
            else if (value[index] == '\'' && !inString)
            {
                return value[..index];
            }
        }

        return value;
    }

    private static string Unquote(string text) =>
        text.Length >= 2 && (text[0] == '"' && text[^1] == '"' || text[0] == '\'' && text[^1] == '\'') ? text[1..^1] : text;
}
