using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Settings;

/// <summary>
/// Script settings: the variable assignments of the language the script is written in (<c>$Name</c> and <c>$env:NAME</c> in
/// PowerShell, <c>set NAME=</c> in batch, <c>Const Name</c> and <c>Name =</c> in VBScript), named as written, values unquoted.
/// Null when the script assigns nothing, so the caller falls back to the line reader.
/// </summary>
internal static class ScriptSettings
{
    public static ExtractedSettings? Extract(string text)
    {
        var lines = ScriptText.Lines(text).ToList();
        var language = ScriptText.Signals(lines).OrderByDescending(pair => pair.Value).First().Key;
        var builder = new SettingsBuilder();
        foreach (var assignment in ScriptText.Assignments(lines, language))
        {
            if (builder.IsFull)
            {
                break;
            }

            builder.Add(assignment.Name, assignment.Value);
        }

        var settings = builder.Build(SettingsMode.Parsed);
        return settings.Entries.Count == 0 ? null : settings;
    }
}
