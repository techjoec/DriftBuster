using System.Text.Json;

namespace DriftBuster.Backend.Curation;

/// <summary>
/// Reads and writes the curation file (<c>curation.json</c> under the data root by default). Saving writes a temporary file
/// and moves it into place, so a crash never leaves half a file; a file that cannot be read raises instead of being replaced.
/// </summary>
public static class CurationStore
{
    public const string FileName = "curation.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(DriftbusterPaths.GetDataRoot(), FileName);

    /// <summary>The document at <paramref name="path"/>, or an empty one when the file does not exist yet.</summary>
    public static CurationDocument Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return new CurationDocument();
        }

        try
        {
            return Normalise(JsonSerializer.Deserialize<CurationDocument>(File.ReadAllText(path), Options) ?? new CurationDocument());
        }
        catch (JsonException exc)
        {
            throw new InvalidDataException($"The curation file {path} could not be read: {exc.Message}", exc);
        }
    }

    public static void Save(CurationDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
        File.Move(temporary, path, overwrite: true);
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

    // Older or hand-edited files may leave lists out; the rest of the code can count on them being there.
    private static CurationDocument Normalise(CurationDocument document) => document with
    {
        Groups = (document.Groups ?? []).Select(group => group with { Members = group.Members ?? [] }).ToArray(),
        Rules = (document.Rules ?? []).Select(rule => rule with { Groups = rule.Groups ?? [] }).ToArray(),
        Choices = document.Choices ?? [],
        Review = document.Review ?? [],
    };
}
