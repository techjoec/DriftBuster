using System.Globalization;
using System.Text.Json;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Settings;

/// <summary>
/// JSON settings as dotted paths (<c>Logging.LogLevel.Default</c>), array items by position (<c>Servers[2]</c>). Comments, trailing
/// commas and a byte order mark are tolerated (<see cref="ScannedJson.Lenient"/>), as appsettings.json files carry them. Strings
/// are shown as they are, other scalars and empty containers as their JSON text.
/// </summary>
internal static class JsonSettings
{
    public static ExtractedSettings? Extract(string text)
    {
        using var document = ScannedJson.TryParse(text, ScannedJson.Lenient);
        if (document is null)
        {
            return null;
        }

        var builder = new SettingsBuilder();
        Add(document.RootElement, builder);
        return builder.Build(SettingsMode.Parsed);
    }

    // Objects add .key, arrays add [n]; empty containers and scalars are values.
    private static void Add(JsonElement root, SettingsBuilder builder)
    {
        var stack = new Stack<(JsonElement Value, string Path)>();
        stack.Push((root, string.Empty));
        while (stack.Count > 0 && !builder.IsFull)
        {
            var (current, path) = stack.Pop();
            switch (current.ValueKind)
            {
                case JsonValueKind.Object when current.EnumerateObject().Any():
                    foreach (var property in current.EnumerateObject().Reverse())
                    {
                        stack.Push((property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}"));
                    }

                    break;
                case JsonValueKind.Array when current.GetArrayLength() > 0:
                    for (var index = current.GetArrayLength() - 1; index >= 0; index--)
                    {
                        stack.Push((current[index], string.Create(CultureInfo.InvariantCulture, $"{path}[{index + 1}]")));
                    }

                    break;
                default:
                    builder.Add(path.Length == 0 ? "(value)" : path, ScannedJson.ScalarText(current));
                    break;
            }
        }
    }
}
