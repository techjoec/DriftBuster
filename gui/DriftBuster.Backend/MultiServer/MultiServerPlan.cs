using System.Security.Cryptography;
using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.MultiServer;

/// <summary>One host to scan, its roots and its baseline preference.</summary>
public sealed record MultiServerPlan
{
    public required string HostId { get; init; }

    public required string Label { get; init; }

    /// <summary>The roots as given; the runner normalises each with <see cref="LexicalPath.Str"/>.</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    public bool IsPreferred { get; init; }

    /// <summary>Baseline priority; the highest wins among preferred hosts.</summary>
    public int Priority { get; init; }

    /// <summary>Registry keys the scan reads besides the roots; null when none.</summary>
    public MultiServerRegistry? Registry { get; init; }

    /// <summary>Seconds to wait after the host is scanned; null, zero or negative waits not at all.</summary>
    public double? ThrottleSeconds { get; init; }

    /// <summary>
    /// From the GUI plan model: host id trimmed (a random SHA-1 hex digest when empty), label trimmed (the host id when empty),
    /// roots trimmed, blanks dropped, the rest with environment variables and <c>~</c> expanded.
    /// </summary>
    public static MultiServerPlan FromServerScanPlan(ServerScanPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Create(
            plan.HostId,
            plan.Label,
            (plan.Roots ?? []).Select(root => root ?? string.Empty),
            plan.Baseline?.IsPreferred ?? false,
            plan.Baseline?.Priority ?? 0,
            plan.ThrottleSeconds) with
        {
            Registry = MultiServerRegistry.Create(plan.Registry?.Keys, plan.Registry?.Computer, plan.Registry?.CredentialFile),
        };
    }

    private static MultiServerPlan Create(string? rawHostId, string? rawLabel, IEnumerable<string> rawRoots, bool isPreferred, int priority, double? throttle)
    {
        var hostId = (rawHostId ?? string.Empty).Trim();
        if (hostId.Length == 0)
        {
            hostId = Convert.ToHexStringLower(SHA1.HashData(RandomNumberGenerator.GetBytes(16)));
        }

        var label = (string.IsNullOrEmpty(rawLabel) ? hostId : rawLabel).Trim();
        if (label.Length == 0)
        {
            label = hostId;
        }

        var roots = new List<string>();
        foreach (var entry in rawRoots)
        {
            var text = entry.Trim();
            if (text.Length > 0)
            {
                roots.Add(EngineOsPath.ExpandUser(EngineOsPath.ExpandVars(text)));
            }
        }

        return new MultiServerPlan
        {
            HostId = hostId,
            Label = label,
            Roots = roots,
            IsPreferred = isPreferred,
            Priority = priority,
            ThrottleSeconds = throttle,
        };
    }

    internal static string Sha1Hex(string text) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text)));
}
