using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// <c>registry.scan</c>: installed application enumeration from the Uninstall keys, registry root suggestions for an application
/// token, and the breadth-first value search. Every function takes an <see cref="IRegistryBackend"/>; without one the Windows
/// backend is used, and off Windows <see cref="DefaultBackend"/> raises Python's <c>RuntimeError</c> text.
/// </summary>
public static partial class RegistryScan
{
    internal const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string UninstallPathWow64 = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly PythonPattern VendorSplit = PythonPattern.Compile(@"[\s_-]+");

    private static readonly (string Hive, string Base, string? View)[] UninstallProbes =
    [
        ("HKLM", UninstallPath, "64"),
        ("HKLM", UninstallPathWow64, "32"),
        ("HKCU", UninstallPath, null),
    ];

    /// <summary>
    /// <c>scan.is_windows</c> as <see cref="DefaultBackend"/> looks it up; tests swap it as Python monkeypatches the module attribute.
    /// </summary>
    internal static Func<bool> IsWindowsProbe { get; set; } = PlatformIsWindows;

    /// <summary><c>is_windows()</c>: the process runs on Windows (<c>sys.platform</c> <c>win32</c> or <c>cygwin</c>).</summary>
    public static bool PlatformIsWindows() => OperatingSystem.IsWindows();

    /// <summary><c>is_windows()</c> through <see cref="IsWindowsProbe"/>.</summary>
    public static bool IsWindows() => IsWindowsProbe();

    /// <summary><c>_default_backend()</c>: the Windows backend.</summary>
    /// <exception cref="PlatformNotSupportedException">Python's <c>RuntimeError("Windows Registry scanning requires Windows platform")</c>.</exception>
    public static IRegistryBackend DefaultBackend()
    {
        if (!IsWindows() || !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Registry scanning requires Windows platform");
        }

        return new WinRegistryBackend();
    }

    /// <summary>
    /// <c>enumerate_installed_apps(backend=backend)</c>: every Uninstall subkey with a non-blank <c>DisplayName</c> from HKLM 64-bit,
    /// HKLM Wow6432Node 32-bit and HKCU, the first of each <c>(hive, key_path)</c> kept, sorted by lower-cased display name then hive.
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

                var displayName = PythonText.Strip(TruthyText(values, "DisplayName") ?? string.Empty);
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
        var unique = apps.Where(app => seen.Add((app.Hive, app.KeyPath))).ToList();
        PythonSort<RegistryApp>.Sort(unique, AppLessThan);
        return unique.AsReadOnly();
    }

    // str(values.get(name)) when the value is truthy, else None.
    private static string? TruthyText(Dictionary<string, object?> values, string name)
        => values.TryGetValue(name, out var value) && PythonBuiltins.IsTruthy(value) ? PythonRepr.Str(value) : null;

    // (a.display_name.lower(), a.hive) < (b.display_name.lower(), b.hive)
    private static bool AppLessThan(RegistryApp left, RegistryApp right)
    {
        var byName = PathText.CompareCodePoints(PythonText.Lower(left.DisplayName), PythonText.Lower(right.DisplayName));
        return byName != 0 ? byName < 0 : PathText.CompareCodePoints(left.Hive, right.Hive) < 0;
    }

    /// <summary>
    /// <c>_candidate_vendor_app_pairs(app_name)</c>: <c>(first word, remaining words joined by " ")</c> when the name splits on runs of
    /// whitespace, "_" and "-" into two or more words, then <c>("", app_name)</c>.
    /// </summary>
    internal static IReadOnlyList<(string Vendor, string Product)> CandidateVendorAppPairs(string appName)
    {
        var parts = RegistryPython.Split(VendorSplit, appName).Where(part => part.Length > 0).ToList();
        var pairs = new List<(string, string)>();
        if (parts.Count >= 2)
        {
            pairs.Add((parts[0], string.Join(' ', parts.Skip(1))));
        }

        pairs.Add((string.Empty, appName));
        return pairs;
    }

    /// <summary>
    /// <c>find_app_registry_roots(app_token, installed=installed)</c>: for each installed app whose lower-cased display name or
    /// publisher holds the stripped, lower-cased token, the HKCU, HKLM (in the app's view) and HKLM Wow6432Node (32-bit) software
    /// keys of each vendor/product pair and the app's Uninstall key; then the same three software keys built from the stripped token;
    /// duplicates dropped in order.
    /// </summary>
    public static IReadOnlyList<RegistryRoot> FindAppRegistryRoots(string appToken, IReadOnlyList<RegistryApp>? installed = null)
    {
        ArgumentNullException.ThrowIfNull(appToken);
        var token = PythonText.Lower(PythonText.Strip(appToken));
        var candidates = new List<RegistryRoot>();
        foreach (var app in installed ?? [])
        {
            if (!PythonText.Contains(PythonText.Lower(app.DisplayName), token)
                && !(!string.IsNullOrEmpty(app.Publisher) && PythonText.Contains(PythonText.Lower(app.Publisher), token)))
            {
                continue;
            }

            var appView = app.View is "32" or "64" ? app.View : null;
            foreach (var (vendor, product) in CandidateVendorAppPairs(app.DisplayName))
            {
                var suffix = string.Join('\\', new[] { PythonText.Strip(vendor), PythonText.Strip(product) }.Where(segment => segment.Length > 0));
                if (suffix.Length > 0)
                {
                    candidates.Add(new RegistryRoot("HKCU", $"Software\\{suffix}"));
                    candidates.Add(new RegistryRoot("HKLM", $"Software\\{suffix}", appView));
                    candidates.Add(new RegistryRoot("HKLM", $"Software\\Wow6432Node\\{suffix}", "32"));
                }
            }

            candidates.Add(new RegistryRoot(app.Hive, app.KeyPath, appView));
        }

        var baseSuffix = PythonText.Strip(appToken);
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
