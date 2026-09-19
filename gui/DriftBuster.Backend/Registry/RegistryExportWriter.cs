using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// Renders registry keys as a Registry Editor version 5 export, the way regedit writes one: <c>[HKEY_…\path]</c> sections in
/// depth-first order, the default value (<c>@</c>) first and the rest sorted by name, <c>REG_SZ</c> as a quoted string,
/// <c>REG_DWORD</c> as <c>dword:</c>, every other type as <c>hex(n):</c> bytes wrapped at 80 columns with a trailing backslash.
/// Subkeys are sorted, so the same registry state always renders the same text.
/// </summary>
/// <remarks>Derived from publicly documented behavior, not vendor source (Microsoft's documentation of the .reg file syntax).</remarks>
public static class RegistryExportWriter
{
    private const int WrapColumn = 80;

    public static string Render(RegistryRoot root, IReadOnlyList<RegistryTreeNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(nodes);
        var byPath = new Dictionary<string, RegistryTreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.Where(node => string.Equals(node.Hive, root.Hive, StringComparison.Ordinal) && string.Equals(node.View, root.View, StringComparison.Ordinal)))
        {
            byPath.TryAdd(node.Path, node);
        }

        var builder = new StringBuilder("Windows Registry Editor Version 5.00\n");
        if (byPath.TryGetValue(root.Path, out var top))
        {
            AppendKey(builder, top, byPath);
        }

        return builder.ToString();
    }

    internal static string HiveName(string hive) => hive switch
    {
        "HKLM" => "HKEY_LOCAL_MACHINE",
        "HKCU" => "HKEY_CURRENT_USER",
        _ => hive,
    };

    private static void AppendKey(StringBuilder builder, RegistryTreeNode node, Dictionary<string, RegistryTreeNode> byPath)
    {
        builder.Append('\n').Append('[').Append(HiveName(node.Hive)).Append('\\').Append(node.Path).Append("]\n");
        foreach (var value in node.Values.OrderBy(value => value.Name.Length == 0 ? 0 : 1).ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            AppendValue(builder, value);
        }

        foreach (var child in node.Subkeys.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (byPath.TryGetValue($"{node.Path}\\{child}", out var childNode))
            {
                AppendKey(builder, childNode, byPath);
            }
        }
    }

    private static void AppendValue(StringBuilder builder, RegistryRawValue value)
    {
        var name = value.Name.Length == 0 ? "@" : Quote(value.Name);
        if (value.Type == RegistryValueDecoder.RegSz)
        {
            builder.Append(name).Append('=').Append(Quote(Utf16String(value.Data))).Append('\n');
            return;
        }

        if (value.Type == RegistryValueDecoder.RegDword && value.Data.Length == sizeof(uint))
        {
            builder.Append(name).Append("=dword:").Append(BinaryPrimitives.ReadUInt32LittleEndian(value.Data).ToString("x8", CultureInfo.InvariantCulture)).Append('\n');
            return;
        }

        var prefix = value.Type == RegistryValueDecoder.RegBinary ? $"{name}=hex:" : string.Create(CultureInfo.InvariantCulture, $"{name}=hex({value.Type:x}):");
        AppendHex(builder, prefix, value.Data);
    }

    // regedit's layout: bytes as two hex digits joined by commas, a line ending in ",\" once it would pass 80 columns, and
    // continuation lines indented by two spaces.
    private static void AppendHex(StringBuilder builder, string prefix, byte[] data)
    {
        builder.Append(prefix);
        var column = prefix.Length;
        for (var index = 0; index < data.Length; index++)
        {
            var last = index == data.Length - 1;
            var piece = data[index].ToString("x2", CultureInfo.InvariantCulture) + (last ? string.Empty : ",");
            if (column + piece.Length > WrapColumn - 1 && !last)
            {
                builder.Append("\\\n  ");
                column = 2;
            }

            builder.Append(piece);
            column += piece.Length;
        }

        builder.Append('\n');
    }

    private static string Utf16String(byte[] data)
    {
        var text = Encoding.Unicode.GetString(data, 0, data.Length - (data.Length % 2));
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
