using System.Globalization;

using DriftBuster.Backend.Detection.Plugins;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Settings;

/// <summary>
/// JSON settings as dotted paths (<c>Logging.LogLevel.Default</c>), array items by position (<c>Servers[2]</c>). Comments and a
/// byte order mark are tolerated, as appsettings.json files carry them. Strings are shown as they are, other values as JSON.
/// </summary>
internal static class JsonSettings
{
    public static ExtractedSettings? Extract(string text)
    {
        var body = text.TrimStart('﻿');
        if (!EngineJson.TryLoads(body, out var value))
        {
            var (cleaned, removed) = JsonPlugin.StripJsonComments(body);
            if (!removed || !EngineJson.TryLoads(cleaned, out value))
            {
                return null;
            }
        }

        var builder = new SettingsBuilder();
        Add(value, string.Empty, builder);
        return builder.Build(SettingsMode.Parsed);
    }

    // Converts the parsed JSON into entries: objects add .key, arrays add [n]; empty containers and scalars are values.
    private static void Add(object? value, string path, SettingsBuilder builder)
    {
        var stack = new Stack<(object? Value, string Path)>();
        stack.Push((value, path));
        while (stack.Count > 0 && !builder.IsFull)
        {
            var (current, currentPath) = stack.Pop();
            switch (current)
            {
                case OrderedDictionary<string, object?> { Count: > 0 } map:
                    foreach (var (key, child) in map.Reverse())
                    {
                        stack.Push((child, currentPath.Length == 0 ? key : $"{currentPath}.{key}"));
                    }

                    break;
                case List<object?> { Count: > 0 } list:
                    for (var index = list.Count - 1; index >= 0; index--)
                    {
                        stack.Push((list[index], string.Create(CultureInfo.InvariantCulture, $"{currentPath}[{index + 1}]")));
                    }

                    break;
                default:
                    builder.Add(currentPath.Length == 0 ? "(value)" : currentPath, Scalar(current));
                    break;
            }
        }
    }

    internal static string Scalar(object? value) => value switch
    {
        string text => text,
        _ => Canonicaliser.Dumps(value, indent: false, ensureAscii: false, sortKeys: true),
    };
}
