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

    /// <summary>The roots as given; the runner spells each as <c>Path(root)</c> does (<see cref="LexicalPath.Str"/>).</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    public bool IsPreferred { get; init; }

    /// <summary><c>BaselinePreference.priority</c>: a Python int, unbounded.</summary>
    public BigInteger Priority { get; init; }

    /// <summary>Registry keys the scan reads besides the roots; null when none.</summary>
    public MultiServerRegistry? Registry { get; init; }

    /// <summary>Seconds to wait after the host is scanned; null, zero or negative waits not at all.</summary>
    public double? ThrottleSeconds { get; init; }

    /// <summary>
    /// <c>Plan.from_mapping</c> over the GUI's plan model: the host id stripped (a random SHA-1 hex digest when empty), the label
    /// stripped (the host id when empty), every root stripped, blank roots dropped, and the rest expanded with <c>expandvars</c>
    /// then <c>expanduser</c>.
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
    /// <c>multi_server._build_plans(request)</c> over a decoded JSON request (<see cref="EngineJson"/> values): <c>request.get("plans")
    /// or []</c> must be a list or a str (a str yields no plans), entries that are not mappings are skipped, and each mapping goes
    /// through <see cref="FromMapping"/>. A <c>plans</c> value of any other type raises <see cref="CommandExitException"/>
    /// (<c>'plans' must be an array</c>); a request that is not a mapping raises <see cref="InvalidDataException"/>, as does every
    /// error <see cref="FromMapping"/> raises.
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
    /// <c>Plan.from_mapping(payload)</c> with Python's coercions: <c>str(host_id or "")</c> and <c>str(label or host_id)</c>
    /// (<see cref="EngineRepr.Str"/>), <c>for entry in roots or []</c> with <c>str(entry or "")</c>, <c>bool(is_preferred)</c>,
    /// <c>int(priority)</c>, <c>float(throttle_seconds)</c> (null when the value is not a number) and a
    /// truthy <c>baseline</c> or <c>export</c> that must be a mapping. <c>scope</c>, <c>role</c>, the export flags and
    /// <c>cached_at</c> are read by Python but never used by the runner, and none of their coercions can raise.
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

    // float(throttle_value), None when the value is not a number; an OverflowException propagates.
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
