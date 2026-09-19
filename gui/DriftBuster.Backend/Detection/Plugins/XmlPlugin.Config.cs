using System.Text.Json.Nodes;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Framework configuration variants: filename and section roles plus XML-Document-Transform detection.</summary>
public sealed partial class XmlPlugin
{
    private static readonly string[] WebHintKeywords = ["system.web", "system.webserver"];
    private static readonly string[] AppHintKeywords = ["startup", "supportedruntime", "assemblybinding"];

    private sealed class ConfigRole
    {
        public string Role { get; set; } = "generic";

        public double Confidence { get; set; } = 0.85;

        public bool InferredTransform { get; set; }

        public string? TransformScope { get; set; }

        public List<string> TransformStages { get; } = [];
    }

    /// <summary>Derives a variant for framework configuration style XML files; <paramref name="path"/> is null when the caller had no filename to offer.</summary>
    private static (string Variant, double Confidence) ClassifyConfigVariant(
        string? path,
        string text,
        List<string> reasons,
        JsonObject metadata)
    {
        if (path is not null)
        {
            metadata.TryAdd("config_original_filename", PathText.Name(path));
        }

        var role = DetectConfigRole(path, text, reasons);

        var transformNamespace = HasXdtNamespaceDeclaration(text);
        var transformAttribute = HasXdtTransformAttribute(text);
        var isTransform = role.InferredTransform || transformNamespace || transformAttribute;

        if (transformNamespace)
        {
            AddReason(reasons, "Detected XML-Document-Transform namespace declaration (xdt)");
        }

        if (transformAttribute)
        {
            AddReason(reasons, "Found xdt:Transform attribute indicating config transform instructions");
        }

        var confidence = role.Confidence;
        if (isTransform)
        {
            metadata["config_transform"] = true;
            var scope = role.TransformScope ?? (string.Equals(role.Role, "generic", StringComparison.Ordinal) ? null : role.Role);
            if (!string.IsNullOrEmpty(scope))
            {
                metadata["config_transform_scope"] = scope;
            }

            RecordTransformStages(role.TransformStages, reasons, metadata);
            confidence = Math.Max(confidence, 0.9);
        }

        metadata.TryAdd("config_role", role.Role);

        if (isTransform)
        {
            return (role.Role switch
            {
                "web" => "web-config-transform",
                "app" => "app-config-transform",
                "machine" => "machine-config-transform",
                _ => "config-transform",
            }, confidence);
        }

        return (role.Role switch
        {
            "web" => "web-config",
            "app" => "app-config",
            "machine" => "machine-config",
            _ => "web-or-app-config",
        }, confidence);
    }

    private static void RecordTransformStages(List<string> transformStages, List<string> reasons, JsonObject metadata)
    {
        if (transformStages.Count == 0)
        {
            return;
        }

        var cleanedStages = transformStages.Select(EngineText.Strip).Where(stage => stage.Length > 0).ToList();
        if (cleanedStages.Count == 0)
        {
            return;
        }

        metadata["config_transform_stages"] = JsonNodes.Strings(cleanedStages);
        metadata["config_transform_primary_stage"] = cleanedStages[^1];
        metadata["config_transform_stage_count"] = cleanedStages.Count;
        if (cleanedStages.Count == 1)
        {
            AddReason(reasons, $"Filename stage '{cleanedStages[0]}' indicates transform precedence");
        }
        else
        {
            AddReason(reasons, $"Transform stages applied in order: {string.Join(" -> ", cleanedStages)}");
        }
    }

    /// <summary>
    /// Combines filename and section hints to classify <c>.config</c> roles: filename patterns set the baseline role
    /// before content hints adjust it, so transforms inherit the right scope and files with neither fall back to
    /// "generic".
    /// </summary>
    private static ConfigRole DetectConfigRole(string? path, string text, List<string> reasons)
    {
        var role = new ConfigRole();
        var filename = path is null ? string.Empty : PathText.Name(path);
        var lowered = EngineText.Lower(filename);
        string? assigned = null;
        if (filename.Length > 0)
        {
            assigned = ApplyFilenameRole(role, filename, lowered, reasons);
        }

        if (assigned is null && HasElementNamed(text, WebHintKeywords))
        {
            AddReason(reasons, "Detected web-specific sections such as <system.web> or <system.webServer>");
            assigned = "web";
            role.Confidence = Math.Max(role.Confidence, 0.88);
        }

        if (assigned is null && HasElementNamed(text, AppHintKeywords))
        {
            AddReason(reasons, "Detected application configuration sections like <startup> or <supportedRuntime>");
            assigned = "app";
            role.Confidence = Math.Max(role.Confidence, 0.86);
        }

        role.Role = assigned ?? "generic";
        return role;
    }

    // Assigns the role from the filename; returns it, or null.
    private static string? ApplyFilenameRole(ConfigRole role, string filename, string lowered, List<string> reasons)
    {
        if (IsFilenameIgnoreCase(filename, "web.config"))
        {
            AddReason(reasons, "Filename web.config strongly suggests web-hosted configuration");
            role.Confidence = Math.Max(role.Confidence, 0.9);
            return "web";
        }

        if (IsFilenameIgnoreCase(filename, "app.config"))
        {
            AddReason(reasons, "Filename app.config indicates per-application configuration");
            role.Confidence = Math.Max(role.Confidence, 0.88);
            return "app";
        }

        if (IsFilenameIgnoreCase(filename, "machine.config"))
        {
            AddReason(reasons, "Filename machine.config indicates machine-wide configuration");
            role.Confidence = Math.Max(role.Confidence, 0.9);
            return "machine";
        }

        var scope = MatchTransformFilename(filename);
        if (scope is not null)
        {
            return ApplyTransformFilename(role, filename, lowered, scope, reasons);
        }

        if (HasAssemblyConfigSuffix(lowered))
        {
            AddReason(reasons, "Filename ending with .exe.config or .dll.config typically ships beside framework binaries");
            role.Confidence = Math.Max(role.Confidence, 0.88);
            return "app";
        }

        return null;
    }

    private static string? ApplyTransformFilename(ConfigRole role, string filename, string lowered, string scope, List<string> reasons)
    {
        role.InferredTransform = true;
        role.TransformScope = scope;
        // The lowered name ends with ".config" only when the original does (no non-ASCII code point lowers to any of
        // its letters alone), so the last seven UTF-16 units are the last seven code points.
        var baseName = lowered.EndsWith(".config", StringComparison.Ordinal) ? filename[..^7] : filename;
        var parts = baseName.Split('.').Where(segment => segment.Length > 0).ToList();
        if (parts.Count > 1)
        {
            role.TransformStages.AddRange(parts.Skip(1));
        }

        string? assigned = scope switch
        {
            "web" => "web",
            "app" => "app",
            _ => null,
        };
        AddReason(reasons, "Filename pattern web|app.*.config suggests a build-specific transform");
        role.Confidence = Math.Max(role.Confidence, string.Equals(scope, "app", StringComparison.Ordinal) ? 0.88 : 0.89);
        return assigned;
    }
}
