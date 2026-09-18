using System.Globalization;
using System.Xml.Linq;

using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Settings;

/// <summary>
/// XML settings as element paths below the document element. .NET configuration conventions come first:
/// <c>&lt;add key="k" value="v"/&gt;</c> is <c>parent:k = v</c> and <c>&lt;add name="n" connectionString="c"/&gt;</c> is
/// <c>parent:n = c</c>. Otherwise an element is named by its <c>key</c>, <c>name</c> or <c>id</c> attribute when it has one
/// (<c>target[file]</c>), else by position when siblings share its name (<c>server[2]</c>); attributes are <c>path@attr</c> and
/// text content is the element's own value.
/// </summary>
internal static class XmlSettings
{
    private static readonly string[] IdentityAttributes = ["key", "name", "id"];

    public static ExtractedSettings? Extract(string text)
    {
        var root = DefusedXmlParser.ParseCanonicalTree(StripProlog(text));
        if (root is null)
        {
            return null;
        }

        var builder = new SettingsBuilder();
        Walk(root, string.Empty, builder);
        return builder.Build(SettingsMode.Parsed);
    }

    // The declaration and DOCTYPE sit outside the document element and carry no settings.
    private static string StripProlog(string text)
    {
        var start = text.IndexOf('<', StringComparison.Ordinal);
        while (start >= 0 && start + 1 < text.Length && (text[start + 1] == '?' || text[start + 1] == '!') && !text.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
        {
            var end = text.IndexOf('>', start);
            if (end < 0)
            {
                return text;
            }

            start = text.IndexOf('<', end);
        }

        return start > 0 ? text[start..] : text;
    }

    private static void Walk(XElement element, string path, SettingsBuilder builder)
    {
        var positions = element.Elements()
            .GroupBy(child => child.Name.LocalName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, _ => 0, StringComparer.Ordinal);

        foreach (var child in element.Elements())
        {
            if (builder.IsFull)
            {
                return;
            }

            if (TryConvention(child, path, builder))
            {
                continue;
            }

            var name = child.Name.LocalName;
            var identity = IdentityAttribute(child);
            string segment;
            if (identity is not null)
            {
                segment = $"{name}[{identity.Value}]";
            }
            else if (positions.TryGetValue(name, out var seen))
            {
                positions[name] = seen + 1;
                segment = string.Create(CultureInfo.InvariantCulture, $"{name}[{seen + 1}]");
            }
            else
            {
                segment = name;
            }

            var childPath = path.Length == 0 ? segment : $"{path}/{segment}";
            foreach (var attribute in child.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration && attribute != identity))
            {
                builder.Add($"{childPath}@{attribute.Name.LocalName}", attribute.Value);
            }

            var text = string.Concat(child.Nodes().OfType<XText>().Select(node => node.Value)).Trim();
            var hasChildren = child.Elements().Any();
            if (text.Length > 0)
            {
                builder.Add(childPath, text);
            }
            else if (!hasChildren && !child.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration && attribute != identity))
            {
                // An element with nothing inside still says something (<clear/>, <remove/>): record that it is there.
                builder.Add(childPath, "(present)");
            }

            Walk(child, childPath, builder);
        }
    }

    // <add key="k" value="v"/> and <add name="n" connectionString="c" providerName="p"/>.
    private static bool TryConvention(XElement element, string path, SettingsBuilder builder)
    {
        if (!string.Equals(element.Name.LocalName, "add", StringComparison.Ordinal) || element.HasElements)
        {
            return false;
        }

        var prefix = path.Length == 0 ? string.Empty : path + ":";
        if (element.Attribute("key") is { } key && element.Attribute("value") is { } value)
        {
            builder.Add(prefix + key.Value, value.Value);
            AddRest(element, prefix + key.Value, builder, "key", "value");
            return true;
        }

        if (element.Attribute("name") is { } name && element.Attribute("connectionString") is { } connection)
        {
            builder.Add(prefix + name.Value, connection.Value);
            AddRest(element, prefix + name.Value, builder, "name", "connectionString");
            return true;
        }

        return false;
    }

    private static void AddRest(XElement element, string settingPath, SettingsBuilder builder, params string[] used)
    {
        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration && !used.Contains(attribute.Name.LocalName, StringComparer.Ordinal)))
        {
            builder.Add($"{settingPath}@{attribute.Name.LocalName}", attribute.Value);
        }
    }

    private static XAttribute? IdentityAttribute(XElement element)
    {
        foreach (var candidate in IdentityAttributes)
        {
            if (element.Attributes().FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, candidate, StringComparison.OrdinalIgnoreCase)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
