using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// Installed-app enumeration from the Uninstall keys, registry root suggestions for an app token, and the breadth-first value
/// search. Each takes an <see cref="IRegistryBackend"/>; without one the Windows backend is used, which throws
/// <see cref="PlatformNotSupportedException"/> off Windows.
/// </summary>
public static partial class RegistryScan
{
    internal const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string UninstallPathWow64 = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    [GeneratedRegex(@"[\s_-]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VendorSplit();

    internal static readonly (string Hive, string Base, string? View)[] UninstallProbes =
    [
        ("HKLM", UninstallPath, "64"),
        ("HKLM", UninstallPathWow64, "32"),
        ("HKCU", UninstallPath, null),
    ];

    /// <summary>Platform check <see cref="DefaultBackend"/> uses (test seam).</summary>
    internal static Func<bool> IsWindowsProbe { get; set; } = PlatformIsWindows;

    public static bool PlatformIsWindows() => OperatingSystem.IsWindows();

    public static bool IsWindows() => IsWindowsProbe();

    /// <exception cref="PlatformNotSupportedException"><c>Windows Registry scanning requires Windows platform</c>.</exception>
    public static IRegistryBackend DefaultBackend()
    {
        if (!IsWindows() || !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Registry scanning requires Windows platform");
        }

        return new WinRegistryBackend();
    }

    /// <summary>
    /// Uninstall subkeys with a non-blank <c>DisplayName</c> from HKLM 64-bit, HKLM Wow6432Node 32-bit and HKCU; the first of each
    /// (hive, key path) kept; sorted by lower-cased name, then hive.
    /// </summary>
    public static IReadOnlyList<RegistryApp> EnumerateInstalledApps(IRegistryBackend? backend = null)
    {
        backend ??= DefaultBackend();
        var apps = new List<RegistryApp>();
        foreach (var (hive, basePath, view) in UninstallProbes)
        {
            foreach (var subkey in backend.EnumSubkeys(hive, basePath, view))
            {
                var keyPath = $"{basePath}\\{subkey}";
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (name, data) in backend.EnumValues(hive, keyPath, view))
                {
                    values[name] = data;
                }

                var displayName = (TruthyText(values, "DisplayName") ?? string.Empty).Trim();
                if (displayName.Length == 0)
                {
                    continue;
                }

                apps.Add(new RegistryApp(
                    displayName,
                    keyPath,
                    hive,
                    TruthyText(values, "Publisher"),
                    TruthyText(values, "DisplayVersion"),
                    TruthyText(values, "UninstallString"),
                    TruthyText(values, "InstallLocation"),
                    view ?? "auto"));
            }
        }

        var seen = new HashSet<(string, string)>();
        var codePoints = StringComparer.Ordinal;
        return apps
            .Where(app => seen.Add((app.Hive, app.KeyPath)))
            .OrderBy(app => app.DisplayName.ToLowerInvariant(), codePoints)
            .ThenBy(app => app.Hive, codePoints)
            .ToList()
            .AsReadOnly();
    }

    // The value's text when it has any (not missing, empty or zero), else null.
    private static string? TruthyText(Dictionary<string, object?> values, string name)
        => values.TryGetValue(name, out var value) && value is not 0UL && ValueText(value) is { Length: > 0 } text ? text : null;

    /// <summary>
    /// (first word, remaining words joined by " ") when the name splits on whitespace, "_" and "-" into two or more words, then
    /// ("", name).
    /// </summary>
    internal static IReadOnlyList<(string Vendor, string Product)> CandidateVendorAppPairs(string appName)
    {
        var parts = VendorSplit().Split(appName).Where(part => part.Length > 0).ToList();
        var pairs = new List<(string, string)>();
        if (parts.Count >= 2)
        {
            pairs.Add((parts[0], string.Join(' ', parts.Skip(1))));
        }

        pairs.Add((string.Empty, appName));
        return pairs;
    }

    /// <summary>
    /// For each installed app whose lower-cased name or publisher contains the token: HKCU, HKLM (the app's view) and HKLM
    /// Wow6432Node (32-bit) software keys for each vendor/product pair, plus its Uninstall key; then the same three keys from the
    /// token itself; duplicates dropped in order.
    /// </summary>
    public static IReadOnlyList<RegistryRoot> FindAppRegistryRoots(string appToken, IReadOnlyList<RegistryApp>? installed = null)
    {
        ArgumentNullException.ThrowIfNull(appToken);
        var token = appToken.Trim().ToLowerInvariant();
        var candidates = new List<RegistryRoot>();
        foreach (var app in installed ?? [])
        {
            if (!app.DisplayName.ToLowerInvariant().Contains(token, StringComparison.Ordinal)
                && !(!string.IsNullOrEmpty(app.Publisher) && app.Publisher.ToLowerInvariant().Contains(token, StringComparison.Ordinal)))
            {
                continue;
            }

            var appView = app.View is "32" or "64" ? app.View : null;
            foreach (var (vendor, product) in CandidateVendorAppPairs(app.DisplayName))
            {
                var suffix = string.Join('\\', new[] { vendor.Trim(), product.Trim() }.Where(segment => segment.Length > 0));
                if (suffix.Length > 0)
                {
                    candidates.Add(new RegistryRoot("HKCU", $"Software\\{suffix}"));
                    candidates.Add(new RegistryRoot("HKLM", $"Software\\{suffix}", appView));
                    candidates.Add(new RegistryRoot("HKLM", $"Software\\Wow6432Node\\{suffix}", "32"));
                }
            }

            candidates.Add(new RegistryRoot(app.Hive, app.KeyPath, appView));
        }

        var baseSuffix = appToken.Trim();
        if (baseSuffix.Length > 0)
        {
            candidates.Add(new RegistryRoot("HKCU", $"Software\\{baseSuffix}"));
            candidates.Add(new RegistryRoot("HKLM", $"Software\\{baseSuffix}"));
            candidates.Add(new RegistryRoot("HKLM", $"Software\\Wow6432Node\\{baseSuffix}", "32"));
        }

        var seen = new HashSet<RegistryRoot>();
        return candidates.Where(seen.Add).ToList().AsReadOnly();
    }
}
