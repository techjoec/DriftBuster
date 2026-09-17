using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

internal static partial class VersionSync
{
    private static readonly (string Key, string Plugin)[] EnginePlugins =
    [
        ("json", "JsonPlugin.cs"),
        ("ini", "IniPlugin.cs"),
        ("xml", "XmlPlugin.cs"),
        ("yaml", "YamlPlugin.cs"),
        ("toml", "TomlPlugin.cs"),
        ("text", "TextPlugin.cs"),
    ];

    /// <summary>
    /// The updates <c>main()</c> makes, yielded one at a time so a <c>formats</c> value that is not a mapping raises where Python's
    /// <c>formats.get</c> raises, after the earlier files are updated.
    /// </summary>
    public static IEnumerable<VersionUpdate> Updates(string root, IReadOnlyDictionary<string, object?> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var formats = versions.TryGetValue("formats", out var value) ? value : new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        string Format(string key) => Str(PythonBuiltins.Get(formats, key));

        yield return new(At(root, "pyproject.toml"), "version\\s*=\\s*\"[^\"]+\"", $"version = \"{Str(versions["core"])}\"", 1);
        yield return new(
            At(root, "Directory.Build.props"),
            "<DriftBusterCoreVersion>[^<]+</DriftBusterCoreVersion>",
            $"<DriftBusterCoreVersion>{Str(versions["core"])}</DriftBusterCoreVersion>",
            1);
        yield return new(At(root, "src", "driftbuster", "catalog.py"), "version=\"[^\"]+\"", $"version=\"{Str(versions["catalog"])}\"", 1);
        yield return new(
            At(root, "gui", "GuiVersion.props"),
            "<DriftBusterGuiVersion>[^<]+</DriftBusterGuiVersion>",
            $"<DriftBusterGuiVersion>{Str(versions["gui"])}</DriftBusterGuiVersion>",
            1);
        var psd1 = At(root, "cli", "DriftBuster.PowerShell", "DriftBuster.psd1");
        yield return new(psd1, "ModuleVersion\\s*=\\s*'[^']+'", $"ModuleVersion     = '{Str(versions["powershell"])}'", 1);
        yield return new(psd1, "[ \\t]*BackendVersion\\s*=\\s*'[^']+'", $"        BackendVersion = '{Str(versions["powershell"])}'", 1);

        yield return new(At(root, "src", "driftbuster", "formats", "ini", "plugin.py"), "version: str = \"[^\"]+\"", $"version: str = \"{Format("ini")}\"", 1);
        yield return new(At(root, "src", "driftbuster", "formats", "json", "plugin.py"), "version: str = \"[^\"]+\"", $"version: str = \"{Format("json")}\"", 1);
        if (PythonBuiltins.IsTruthy(PythonBuiltins.Get(formats, "xml")))
        {
            yield return new(At(root, "src", "driftbuster", "formats", "xml", "plugin.py"), "version: str = \"[^\"]+\"", $"version: str = \"{Format("xml")}\"", 1);
        }

        foreach (var update in EngineUpdates(root, Str(versions["catalog"]), formats))
        {
            yield return update;
        }

        foreach (var update in DocUpdates(root, Str(versions["catalog"]), Format, Str(versions["core"])))
        {
            yield return update;
        }
    }

    /// <summary>
    /// The C# engine the tools ship: the catalog version, the version of every plugin <c>formats</c> names, and the tests that pin the
    /// catalog version. A format key without a plugin file here (or an empty value) is left alone.
    /// </summary>
    private static IEnumerable<VersionUpdate> EngineUpdates(string root, string catalogVersion, object? formats)
    {
        var detection = At(root, "gui", "DriftBuster.Backend", "Detection");
        yield return new(At(detection, "Catalog", "DetectionCatalogData.cs"), "Version: \"[^\"]+\"", $"Version: \"{catalogVersion}\"", 1);

        var tests = At(root, "gui", "DriftBuster.Backend.Tests", "Detection");
        yield return new(At(tests, "CatalogTests.cs"), "Catalog\\.Version\\.Should\\(\\)\\.Be\\(\"[^\"]+\"\\)", $"Catalog.Version.Should().Be(\"{catalogVersion}\")", 1);
        yield return new(
            At(tests, "DetectorTests.cs"),
            "\\[\"catalog_version\"\\]\\.Should\\(\\)\\.Be\\(\"[^\"]+\"\\)",
            $"[\"catalog_version\"].Should().Be(\"{catalogVersion}\")");

        foreach (var (key, plugin) in EnginePlugins)
        {
            var version = PythonBuiltins.Get(formats, key);
            if (PythonBuiltins.IsTruthy(version))
            {
                var pattern = "public string Version => \"[^\"]+\"";
                yield return new(At(detection, "Plugins", plugin), pattern, $"public string Version => \"{Str(version)}\"", 1);
            }
        }
    }

    private static IEnumerable<VersionUpdate> DocUpdates(string root, string catalogVersion, Func<string, string> format, string coreVersion)
    {
        var detectionTypes = At(root, "docs", "detection-types.md");
        yield return new(detectionTypes, "DETECTION_CATALOG` \\(v[^)]+\\)", $"DETECTION_CATALOG` (v{catalogVersion})", 1);
        yield return new(detectionTypes, "format survey data\\n\\(v[0-9.]+\\)", $"format survey data\n(v{catalogVersion})", 1);
        yield return new(
            detectionTypes,
            "``catalog_version`` \\| Detection catalog version embedded in the match payload\\.\\s+\\| ``[0-9.]+``",
            $"``catalog_version`` | Detection catalog version embedded in the match payload.     | ``{catalogVersion}``",
            1);
        yield return new(detectionTypes, "\"catalog_version\": \"[^\"]+\"", $"\"catalog_version\": \"{catalogVersion}\"");

        var guide = At(root, "docs", "format-addition-guide.md");
        yield return new(guide, "JsonPlugin`\\s+\\|\\s+200\\s+\\|\\s+[0-9.]+", $"JsonPlugin`                  | 200      | {format("json")}", 1);
        yield return new(guide, "IniPlugin`\\s+\\|\\s+170\\s+\\|\\s+[0-9.]+", $"IniPlugin`                    | 170      | {format("ini")}", 1);
        var support = At(root, "docs", "format-support.md");
        yield return new(support, "json\\s+\\|\\s+[0-9.]+", $"json   | {format("json")}");
        yield return new(support, "ini\\s+\\|\\s+[0-9.]+", $"ini    | {format("ini")}");

        yield return new(At(root, "docs", "customization.md"), "version = \"[^\"]+\"", $"version = \"{coreVersion}\"", 1);
        yield return new(At(root, "notes", "snippets", "xml-config-diffs.md"), "\"catalog_version\": \"[^\"]+\"", $"\"catalog_version\": \"{catalogVersion}\"");
        yield return new(At(root, "tests", "core", "test_detector.py"), "catalog_version\"\\] == \"[^\"]+\"", $"catalog_version\"] == \"{catalogVersion}\"", 1);

        yield return new(At(root, "CLA", "INDIVIDUAL.md"), "\\*\\*Version:\\*\\* [0-9.]+", $"**Version:** {coreVersion}", 1);
        yield return new(At(root, "CLA", "ENTITY.md"), "\\*\\*Version:\\*\\* [0-9.]+", $"**Version:** {coreVersion}", 1);
    }
}
