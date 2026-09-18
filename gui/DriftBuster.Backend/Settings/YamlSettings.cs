using System.Globalization;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DriftBuster.Backend.Settings;

/// <summary>
/// YAML settings through YamlDotNet: mapping keys joined with dots (<c>agent.poll.seconds</c>), sequence items by position
/// (<c>servers[2]</c>). A second document in the same file is prefixed <c>doc2.</c>; aliases resolve to their anchor's value.
/// </summary>
internal static class YamlSettings
{
    public static ExtractedSettings? Extract(string text)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException)
        {
            return null;
        }

        var builder = new SettingsBuilder();
        for (var index = 0; index < stream.Documents.Count; index++)
        {
            var prefix = index == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"doc{index + 1}");
            Add(stream.Documents[index].RootNode, prefix, builder);
        }

        return builder.Build(SettingsMode.Parsed);
    }

    private static void Add(YamlNode root, string rootPath, SettingsBuilder builder)
    {
        var stack = new Stack<(YamlNode Node, string Path)>();
        stack.Push((root, rootPath));
        while (stack.Count > 0 && !builder.IsFull)
        {
            var (node, path) = stack.Pop();
            switch (node)
            {
                case YamlMappingNode { Children.Count: > 0 } mapping:
                    foreach (var (key, value) in mapping.Children.Reverse())
                    {
                        var name = key is YamlScalarNode scalar ? scalar.Value ?? string.Empty : key.ToString();
                        stack.Push((value, path.Length == 0 ? name : $"{path}.{name}"));
                    }

                    break;
                case YamlSequenceNode { Children.Count: > 0 } sequence:
                    for (var index = sequence.Children.Count - 1; index >= 0; index--)
                    {
                        stack.Push((sequence.Children[index], string.Create(CultureInfo.InvariantCulture, $"{path}[{index + 1}]")));
                    }

                    break;
                case YamlScalarNode scalar:
                    builder.Add(path.Length == 0 ? "(value)" : path, scalar.Value ?? string.Empty);
                    break;
                case YamlMappingNode:
                    builder.Add(path.Length == 0 ? "(value)" : path, "{}");
                    break;
                case YamlSequenceNode:
                    builder.Add(path.Length == 0 ? "(value)" : path, "[]");
                    break;
                default:
                    break;
            }
        }
    }
}
