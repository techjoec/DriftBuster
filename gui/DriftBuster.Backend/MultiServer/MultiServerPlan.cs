using System.Numerics;
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

    /// <summary>Baseline priority, arbitrary size.</summary>
    public BigInteger Priority { get; init; }

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

    /// <summary>
    /// Plans from a decoded <c>multi-server</c> request: <c>plans</c> must be a list (a string yields none; any other type throws
    /// <see cref="CommandExitException"/> <c>'plans' must be an array</c>); non-object entries are skipped; each object goes through
    /// <see cref="FromMapping"/>. A non-object request throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static IReadOnlyList<MultiServerPlan> BuildPlans(object? request)
    {
        var payload = EngineBuiltins.Get(request, "plans");
        if (!EngineBuiltins.IsTruthy(payload))
        {
            return [];
        }

        if (payload is not (string or List<object?>))
        {
            throw new CommandExitException("'plans' must be an array");
        }

        return EngineBuiltins.Iterate(payload)
            .OfType<OrderedDictionary<string, object?>>()
            .Select(FromMapping)
            .ToList();
    }

    /// <summary>
    /// One plan object: <c>host_id</c> and <c>label</c> as text (label defaults to the host id), <c>roots</c> as text entries,
    /// <c>baseline.is_preferred</c> as bool, <c>baseline.priority</c> as integer, <c>throttle_seconds</c> as a number (null otherwise),
    /// optional <c>registry</c>. A truthy <c>baseline</c> or <c>export</c> must be an object. <c>scope</c>, <c>role</c>, export flags and
    /// <c>cached_at</c> are ignored.
    /// </summary>
    public static MultiServerPlan FromMapping(OrderedDictionary<string, object?> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var hostId = EngineBuiltins.Get(payload, "host_id");
        var label = EngineBuiltins.Get(payload, "label");
        var rawRoots = EngineBuiltins.Get(payload, "roots");
        var roots = EngineBuiltins.IsTruthy(rawRoots)
            ? EngineBuiltins.Iterate(rawRoots).Select(entry => EngineBuiltins.IsTruthy(entry) ? EngineRepr.Str(entry) : string.Empty).ToList()
            : [];
        var baseline = EngineBuiltins.Get(payload, "baseline");
        var isPreferred = false;
        var priority = BigInteger.Zero;
        if (EngineBuiltins.IsTruthy(baseline))
        {
            isPreferred = EngineBuiltins.IsTruthy(EngineBuiltins.Get(baseline, "is_preferred"));
            var mapping = (IReadOnlyDictionary<string, object?>)baseline!;
            priority = EngineBuiltins.Int(mapping.TryGetValue("priority", out var given) ? given : 0);
        }

        var export = EngineBuiltins.Get(payload, "export");
        if (EngineBuiltins.IsTruthy(export))
        {
            EngineBuiltins.Get(export, "include_catalog");
        }

        return Create(
            EngineBuiltins.IsTruthy(hostId) ? EngineRepr.Str(hostId) : string.Empty,
            EngineBuiltins.IsTruthy(label) ? EngineRepr.Str(label) : null,
            roots,
            isPreferred,
            priority,
            Throttle(EngineBuiltins.Get(payload, "throttle_seconds"))) with
        {
            Registry = RegistryFromMapping(EngineBuiltins.Get(payload, "registry")),
        };
    }

    // "registry": {"keys": [...], "computer": "...", "credential_file": "..."}; anything that is not a mapping reads no registry.
    private static MultiServerRegistry? RegistryFromMapping(object? value)
    {
        if (value is not OrderedDictionary<string, object?> mapping)
        {
            return null;
        }

        string? Text(string key) => mapping.TryGetValue(key, out var raw) && EngineBuiltins.IsTruthy(raw) ? EngineRepr.Str(raw) : null;
        var keys = mapping.TryGetValue("keys", out var rawKeys) && EngineBuiltins.IsTruthy(rawKeys) && rawKeys is not string
            ? EngineBuiltins.Iterate(rawKeys).Select(entry => EngineBuiltins.IsTruthy(entry) ? EngineRepr.Str(entry) : null)
            : [];
        return MultiServerRegistry.Create(keys, Text("computer"), Text("credential_file"));
    }

    // A number, or null when the value is not one; OverflowException propagates.
    private static double? Throttle(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return EngineBuiltins.Float(value);
        }
        catch (Exception exc) when (exc is InvalidDataException or FormatException)
        {
            return null;
        }
    }

    private static MultiServerPlan Create(string? rawHostId, string? rawLabel, IEnumerable<string> rawRoots, bool isPreferred, BigInteger priority, double? throttle)
    {
        var hostId = EngineText.Strip(rawHostId ?? string.Empty);
        if (hostId.Length == 0)
        {
            hostId = Convert.ToHexStringLower(SHA1.HashData(RandomNumberGenerator.GetBytes(16)));
        }

        var label = EngineText.Strip(string.IsNullOrEmpty(rawLabel) ? hostId : rawLabel);
        if (label.Length == 0)
        {
            label = hostId;
        }

        var roots = new List<string>();
        foreach (var entry in rawRoots)
        {
            var text = EngineText.Strip(entry);
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
