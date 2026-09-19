namespace DriftBuster.Cli.Commands;

internal static partial class VersionSync
{
    /// <summary>
    /// The updates <see cref="Run"/> makes, in order: <c>core</c> versions the backend and the console tool, <c>catalog</c> the detection
    /// catalog, <c>gui</c> the desktop app and <c>powershell</c> the module (whose <c>BackendVersion</c> pins <c>core</c>).
    /// </summary>
    public static IEnumerable<VersionUpdate> Updates(string root, IReadOnlyDictionary<string, object?> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var core = Str(versions["core"]);
        var catalog = Str(versions["catalog"]);

        yield return new(
            At(root, "Directory.Build.props"),
            "<DriftBusterCoreVersion>[^<]+</DriftBusterCoreVersion>",
            $"<DriftBusterCoreVersion>{core}</DriftBusterCoreVersion>",
            1);
        yield return new(
            At(root, "gui", "GuiVersion.props"),
            "<DriftBusterGuiVersion>[^<]+</DriftBusterGuiVersion>",
            $"<DriftBusterGuiVersion>{Str(versions["gui"])}</DriftBusterGuiVersion>",
            1);
        var psd1 = At(root, "cli", "DriftBuster.PowerShell", "DriftBuster.psd1");
        yield return new(psd1, "ModuleVersion\\s*=\\s*'[^']+'", $"ModuleVersion     = '{Str(versions["powershell"])}'", 1);
        yield return new(psd1, "[ \\t]*BackendVersion\\s*=\\s*'[^']+'", $"        BackendVersion = '{core}'", 1);

        yield return new(
            At(root, "gui", "DriftBuster.Backend", "Detection", "Catalog", "DetectionCatalogData.cs"),
            "Version: \"[^\"]+\"",
            $"Version: \"{catalog}\"",
            1);
        var tests = At(root, "gui", "DriftBuster.Backend.Tests", "Detection");
        yield return new(At(tests, "CatalogTests.cs"), "Catalog\\.Version\\.Should\\(\\)\\.Be\\(\"[^\"]+\"\\)", $"Catalog.Version.Should().Be(\"{catalog}\")", 1);
        yield return new(
            At(tests, "DetectorTests.cs"),
            "\\[\"catalog_version\"\\]\\.Should\\(\\)\\.Be\\(\"[^\"]+\"\\)",
            $"[\"catalog_version\"].Should().Be(\"{catalog}\")");

        var detectionTypes = At(root, "docs", "detection-types.md");
        yield return new(detectionTypes, "the catalog \\(v[^)]+\\)", $"the catalog (v{catalog})", 1);
        yield return new(detectionTypes, "format survey data\\n\\(v[0-9.]+\\)", $"format survey data\n(v{catalog})", 1);
        yield return new(
            detectionTypes,
            "`catalog_version` \\| Detection catalog version embedded in the match payload\\.\\s+\\| `[0-9.]+`",
            $"`catalog_version` | Detection catalog version embedded in the match payload.     | `{catalog}`",
            1);
        yield return new(detectionTypes, "\"catalog_version\": \"[^\"]+\"", $"\"catalog_version\": \"{catalog}\"");
        yield return new(At(root, "notes", "snippets", "xml-config-diffs.md"), "\"catalog_version\": \"[^\"]+\"", $"\"catalog_version\": \"{catalog}\"");

        yield return new(At(root, "CLA", "INDIVIDUAL.md"), "\\*\\*Version:\\*\\* [0-9.]+", $"**Version:** {core}", 1);
        yield return new(At(root, "CLA", "ENTITY.md"), "\\*\\*Version:\\*\\* [0-9.]+", $"**Version:** {core}", 1);
    }
}
