using System.Text.Json;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.MultiServer;

/// <summary>
/// Canonical text per host and config (<c>&lt;sha1(host:config)&gt;.json</c>), valid while the signature (host, config, sampling
/// fingerprint, file hash, content type and canonical form version) matches. The cache is rebuilt from the files at any time, so
/// an entry that cannot be read counts as a miss and is rewritten.
/// </summary>
public sealed class DiffCache
{
    public DiffCache(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = Directory.CreateDirectory(root).FullName;
    }

    public string Root { get; }

    public string EntryPath(string hostId, string configId)
        => Path.Join(Root, MultiServerPlan.Sha1Hex($"{hostId}:{configId}") + ".json");

    /// <summary>The cached canonical text, or null when there is no entry for this signature.</summary>
    public string? Load(string hostId, string configId, string signature)
    {
        var path = EntryPath(hostId, configId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var entry = JsonSerializer.Deserialize(File.ReadAllBytes(path), ModelJson.TypeInfo<DiffCacheEntry>());
            return entry is not null && string.Equals(entry.Signature, signature, StringComparison.Ordinal) ? entry.Canonical : null;
        }
        catch (Exception exc) when (exc is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string hostId, string configId, string signature, string canonical)
        => AtomicFile.WriteAllText(EntryPath(hostId, configId), ModelJson.Serialize(new DiffCacheEntry(signature, canonical)));
}
