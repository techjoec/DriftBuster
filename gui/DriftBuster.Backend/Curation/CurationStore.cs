using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Curation;

/// <summary>
/// Reads and writes the curation file (<c>curation.json</c> under the data root by default) strictly through <see cref="ModelJson"/>.
/// Saving writes a temporary file and moves it into place, so a crash never leaves half a file; a file that cannot be read raises
/// <see cref="InvalidDataException"/> naming the file and JSON path and is never replaced.
/// </summary>
public static class CurationStore
{
    public const string FileName = "curation.json";

    public static string DefaultPath => Path.Combine(DriftbusterPaths.GetDataRoot(), FileName);

    /// <summary>The document at <paramref name="path"/>, or an empty one when the file does not exist yet.</summary>
    public static CurationDocument Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return new CurationDocument();
        }

        var document = ModelJson.ReadFile(path, ModelJson.TypeInfo<CurationDocument>());
        return document.SchemaVersion == CurationDocument.CurrentSchemaVersion
            ? document
            : throw new InvalidDataException($"{path}: $.schema_version: {document.SchemaVersion} is not the supported version {CurationDocument.CurrentSchemaVersion}.");
    }

    public static void Save(CurationDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ModelJson.WriteFile(path, document, ModelJson.TypeInfo<CurationDocument>());
    }

    /// <summary>
    /// Adds what <paramref name="incoming"/> has that <paramref name="current"/> lacks: groups and rules by name (an incoming
    /// group's members join the existing group), choices and review items by target, kind and scope.
    /// </summary>
    public static CurationDocument Merge(CurationDocument current, CurationDocument incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        var groups = current.Groups.ToList();
        foreach (var group in incoming.Groups)
        {
            var index = groups.FindIndex(existing => string.Equals(existing.Name, group.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                groups.Add(group);
            }
            else
            {
                groups[index] = groups[index] with { Members = groups[index].Members.Concat(group.Members).Distinct().ToArray() };
            }
        }

        return current with
        {
            Groups = groups,
            Rules = current.Rules.Concat(incoming.Rules.Where(rule => !current.Rules.Any(existing => string.Equals(existing.Name, rule.Name, StringComparison.OrdinalIgnoreCase)))).ToArray(),
            Choices = current.Choices.Concat(incoming.Choices.Where(choice => !current.Choices.Any(existing =>
                existing.Target == choice.Target && string.Equals(existing.Kind, choice.Kind, StringComparison.Ordinal) && string.Equals(existing.Scope, choice.Scope, StringComparison.Ordinal)))).ToArray(),
            Review = current.Review.Concat(incoming.Review.Where(item => !current.Review.Any(existing => existing.Target == item.Target))).ToArray(),
        };
    }
}
