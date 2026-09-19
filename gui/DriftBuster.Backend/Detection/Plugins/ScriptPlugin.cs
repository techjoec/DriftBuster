using System.Text.Json.Nodes;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects Windows script configuration: PowerShell (<c>ps1-shell</c>), batch (<c>batch-script</c>), CMD (<c>cmd-shell</c>) and
/// VBScript (<c>vbscript</c>). Each language's structural signals are counted line by line; the language with the most wins.
/// Two distinct signals are needed when the extension names a script, three when it does not, so prose with a code sample
/// is not claimed. Batch and CMD share their syntax, so <c>.cmd</c> picks <c>cmd-shell</c>.
/// </summary>
/// <remarks>Derived from publicly documented behavior, not vendor source.</remarks>
public sealed class ScriptPlugin : IFormatPlugin
{
    internal const int AnalysisWindow = 200_000;

    private static readonly Dictionary<string, ScriptText.Language> ExtensionLanguages = new(StringComparer.Ordinal)
    {
        [".ps1"] = ScriptText.Language.PowerShell,
        [".psm1"] = ScriptText.Language.PowerShell,
        [".bat"] = ScriptText.Language.Batch,
        [".cmd"] = ScriptText.Language.Batch,
        [".vbs"] = ScriptText.Language.VbScript,
    };

    public string Name => "script";

    public int Priority => 90;

    public string Version => "0.0.1";

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (text is null)
        {
            return null;
        }

        var window = text.Length > AnalysisWindow ? text[..AnalysisWindow] : text;
        var signals = ScriptText.Signals(ScriptText.Lines(window));
        var suffix = PathText.SuffixLower(path);
        var hasExtension = ExtensionLanguages.TryGetValue(suffix, out var extensionLanguage);
        var best = signals.OrderByDescending(pair => pair.Value)
            .ThenByDescending(pair => hasExtension && pair.Key == extensionLanguage)
            .First();
        var needed = hasExtension && best.Key == extensionLanguage ? 2 : 3;
        if (best.Value < needed)
        {
            return null;
        }

        var reasons = new List<string>
        {
            $"Found {best.Value} {Describe(best.Key)} constructs",
        };
        var confidence = 0.5 + (0.1 * Math.Min(best.Value, 3));
        if (hasExtension && best.Key == extensionLanguage)
        {
            reasons.Add($"File extension {suffix} suggests {Describe(best.Key)}");
            confidence += 0.15;
        }

        var variant = best.Key switch
        {
            ScriptText.Language.PowerShell => "ps1-shell",
            ScriptText.Language.VbScript => "vbscript",
            _ => string.Equals(suffix, ".cmd", StringComparison.Ordinal) ? "cmd-shell" : "batch-script",
        };
        var metadata = new JsonObject()
        {
            ["script_language"] = Describe(best.Key),
            ["signal_count"] = (long)best.Value,
        };
        return new DetectionMatch(Name, "script-config", variant, Math.Min(0.95, confidence), reasons, metadata);
    }

    internal static string Describe(ScriptText.Language language) => language switch
    {
        ScriptText.Language.PowerShell => "PowerShell",
        ScriptText.Language.Batch => "batch",
        _ => "VBScript",
    };
}
