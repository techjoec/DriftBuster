using System.Numerics;
using System.Security.Cryptography;
using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.MultiServer;

/// <summary><c>driftbuster.multi_server.Plan</c>: one host to scan, its roots and its baseline preference.</summary>
public sealed record MultiServerPlan
{
    public required string HostId { get; init; }

    public required string Label { get; init; }

    /// <summary>The roots as given; the runner spells each as <c>Path(root)</c> does (<see cref="PythonPurePath.Str"/>).</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    public bool IsPreferred { get; init; }

    /// <summary><c>BaselinePreference.priority</c>: a Python int, unbounded.</summary>
    public BigInteger Priority { get; init; }

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
            plan.ThrottleSeconds);
    }

    /// <summary>
    /// <c>multi_server._build_plans(request)</c> over a decoded JSON request (<see cref="PythonJson"/> values): <c>request.get("plans")
    /// or []</c> must be a list or a str (a str yields no plans), entries that are not mappings are skipped, and each mapping goes
    /// through <see cref="FromMapping"/>. Python's errors are raised with Python's text: a request that is not a mapping
    /// (<c>AttributeError</c>), a <c>plans</c> value of any other type (<c>SystemExit: 'plans' must be an array</c>, as
    /// <see cref="InvalidDataException"/>) and every error <see cref="FromMapping"/> raises.
    /// </summary>
    public static IReadOnlyList<MultiServerPlan> BuildPlans(object? request)
    {
        var payload = PythonBuiltins.Get(request, "plans");
        if (!PythonBuiltins.IsTruthy(payload))
        {
            return [];
        }

        if (payload is not (string or List<object?>))
        {
            throw new InvalidDataException("'plans' must be an array");
        }

        return PythonBuiltins.Iterate(payload)
            .OfType<OrderedDictionary<string, object?>>()
            .Select(FromMapping)
            .ToList();
    }

    /// <summary>
    /// <c>Plan.from_mapping(payload)</c> with Python's coercions: <c>str(host_id or "")</c> and <c>str(label or host_id)</c>
    /// (<see cref="PythonRepr.Str"/>), <c>for entry in roots or []</c> with <c>str(entry or "")</c>, <c>bool(is_preferred)</c>,
    /// <c>int(priority)</c>, <c>float(throttle_seconds)</c> (null when that raises <c>TypeError</c> or <c>ValueError</c>) and a
    /// truthy <c>baseline</c> or <c>export</c> that must be a mapping. <c>scope</c>, <c>role</c>, the export flags and
    /// <c>cached_at</c> are read by Python but never used by the runner, and none of their coercions can raise.
    /// </summary>
    public static MultiServerPlan FromMapping(OrderedDictionary<string, object?> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var hostId = PythonBuiltins.Get(payload, "host_id");
        var label = PythonBuiltins.Get(payload, "label");
        var rawRoots = PythonBuiltins.Get(payload, "roots");
        var roots = PythonBuiltins.IsTruthy(rawRoots)
            ? PythonBuiltins.Iterate(rawRoots).Select(entry => PythonBuiltins.IsTruthy(entry) ? PythonRepr.Str(entry) : string.Empty).ToList()
            : [];
        var baseline = PythonBuiltins.Get(payload, "baseline");
        var isPreferred = false;
        var priority = BigInteger.Zero;
        if (PythonBuiltins.IsTruthy(baseline))
        {
            isPreferred = PythonBuiltins.IsTruthy(PythonBuiltins.Get(baseline, "is_preferred"));
            var mapping = (IReadOnlyDictionary<string, object?>)baseline!;
            priority = PythonBuiltins.Int(mapping.TryGetValue("priority", out var given) ? given : 0);
        }

        var export = PythonBuiltins.Get(payload, "export");
        if (PythonBuiltins.IsTruthy(export))
        {
            PythonBuiltins.Get(export, "include_catalog");
        }

        return Create(
            PythonBuiltins.IsTruthy(hostId) ? PythonRepr.Str(hostId) : string.Empty,
            PythonBuiltins.IsTruthy(label) ? PythonRepr.Str(label) : null,
            roots,
            isPreferred,
            priority,
            Throttle(PythonBuiltins.Get(payload, "throttle_seconds")));
    }

    // float(throttle_value), None when it raises TypeError or ValueError; OverflowError propagates.
    private static double? Throttle(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return PythonBuiltins.Float(value);
        }
        catch (Exception exc) when (exc is PythonTypeException or PythonValueException)
        {
            return null;
        }
    }

    private static MultiServerPlan Create(string? rawHostId, string? rawLabel, IEnumerable<string> rawRoots, bool isPreferred, BigInteger priority, double? throttle)
    {
        var hostId = PythonText.Strip(rawHostId ?? string.Empty);
        if (hostId.Length == 0)
        {
            hostId = Convert.ToHexStringLower(SHA1.HashData(RandomNumberGenerator.GetBytes(16)));
        }

        var label = PythonText.Strip(string.IsNullOrEmpty(rawLabel) ? hostId : rawLabel);
        if (label.Length == 0)
        {
            label = hostId;
        }

        var roots = new List<string>();
        foreach (var entry in rawRoots)
        {
            var text = PythonText.Strip(entry);
            if (text.Length > 0)
            {
                roots.Add(PythonOsPath.ExpandUser(PythonOsPath.ExpandVars(text)));
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
