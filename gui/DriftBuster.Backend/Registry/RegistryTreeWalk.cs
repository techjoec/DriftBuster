using System.Diagnostics;

namespace DriftBuster.Backend.Registry;

/// <summary>The breadth-first walk both readers share: each key once, children in the order the registry lists them.</summary>
internal static class RegistryTreeWalk
{
    public static RegistryTreeRead Walk(IReadOnlyList<RegistryRoot> roots, int maxDepth, Func<RegistryRoot, RegistryTreeNode?> readKey, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var queue = new Queue<(RegistryRoot Key, int Depth)>(roots.Select(root => (root, 0)));
        var seen = new HashSet<(string, string, string?)>();
        var nodes = new List<RegistryTreeNode>();
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nodes.Count >= RegistryTreeLimits.MaxKeys || Stopwatch.GetElapsedTime(started).TotalSeconds >= RegistryTreeLimits.BudgetSeconds)
            {
                return new RegistryTreeRead(nodes, Truncated: true);
            }

            var (key, depth) = queue.Dequeue();
            if (!seen.Add((key.Hive, key.Path.ToUpperInvariant(), key.View)) || readKey(key) is not { } node)
            {
                continue;
            }

            nodes.Add(node);
            if (depth < maxDepth)
            {
                foreach (var child in node.Subkeys)
                {
                    queue.Enqueue((key with { Path = $"{key.Path}\\{child}" }, depth + 1));
                }
            }
        }

        return new RegistryTreeRead(nodes, Truncated: false);
    }
}
