using System.Globalization;
using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Catalog;
using DriftBuster.Backend.Detection.Plugins;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.MultiServer;

/// <summary>
/// The registry part of a host's scan: each key the plan names (or finds from an application name) is read, locally or over
/// WinRM, rendered as a Registry Editor export, and added as a record at <c>registry/&lt;hive&gt;/&lt;path&gt;.reg</c>, so it is
/// compared, listed and diffed like any configuration file.
/// </summary>
public sealed partial class MultiServerRunner
{
    /// <summary>Seam for the reader a plan's registry settings use; null when the registry cannot be read here (not Windows).</summary>
    internal Func<MultiServerRegistry, IRegistryTreeReader?> RegistryReaderFactory { get; set; } = DefaultRegistryReader;

    private sealed record RegistryOutcome(int Keys, string Message, bool Failed);

    private static IRegistryTreeReader? DefaultRegistryReader(MultiServerRegistry registry)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return registry.Computer is { } computer ? new RemoteRegistryTreeReader(computer, registry.CredentialFile) : new LocalRegistryTreeReader();
    }

    private RegistryOutcome ScanRegistry(MultiServerPlan plan, OrderedDictionary<string, ConfigRecord> configs, CancellationToken cancellationToken)
    {
        var registry = plan.Registry!;
        var where = registry.Computer ?? "this computer";
        var reader = RegistryReaderFactory(registry);
        if (reader is null)
        {
            return new RegistryOutcome(0, " Registry keys skipped: reading the registry needs Windows.", Failed: true);
        }

        try
        {
            var (roots, invalid) = ResolveRegistryRoots(registry, reader, cancellationToken);
            var read = reader.Read(roots, RegistryTreeLimits.MaxDepth, cancellationToken);
            var found = new HashSet<(string, string, string?)>(read.Nodes.Select(node => (node.Hive, node.Path.ToUpperInvariant(), node.View)));
            var count = 0;
            for (var index = 0; index < roots.Count; index++)
            {
                var root = roots[index];
                if (!found.Contains((root.Hive, root.Path.ToUpperInvariant(), root.View)))
                {
                    continue;
                }

                var record = BuildRegistryRecord(plan, root, RegistryExportWriter.Render(root, read.Nodes), plan.Roots.Count + index, configs, cancellationToken);
                configs[record.ConfigId] = record;
                count++;
            }

            var message = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $" Read {count} registry key(s) on {where}."));
            if (read.Truncated)
            {
                message.Append(" The registry read stopped at the key or time limit.");
            }

            if (invalid.Count > 0)
            {
                message.Append(" Not registry keys: ").Append(string.Join(", ", invalid)).Append('.');
            }

            return new RegistryOutcome(count, message.ToString(), Failed: false);
        }
        catch (Exception exc) when (exc is not OperationCanceledException and not OutOfMemoryException)
        {
            return new RegistryOutcome(0, TruncateCodePoints($" Registry read failed: {exc.Message}", MaxFailureMessageLength), Failed: true);
        }
    }

    // Keys as given, plus the keys found for each application name from the host's installed applications; a key under another
    // key already listed is dropped, since its values are part of that key's record.
    private static (List<RegistryRoot> Roots, List<string> Invalid) ResolveRegistryRoots(MultiServerRegistry registry, IRegistryTreeReader reader, CancellationToken cancellationToken)
    {
        var roots = new List<RegistryRoot>();
        var apps = new List<string>();
        var invalid = new List<string>();
        foreach (var entry in registry.Keys)
        {
            if (!entry.StartsWith("HK", StringComparison.OrdinalIgnoreCase))
            {
                apps.Add(entry);
                continue;
            }

            try
            {
                roots.Add(RegistryRoot.Parse(entry));
            }
            catch (FormatException)
            {
                invalid.Add(entry);
            }
        }

        if (apps.Count > 0)
        {
            var probes = RegistryScan.UninstallProbes.Select(probe => new RegistryRoot(probe.Hive, probe.Base, probe.View)).ToList();
            var installed = RegistryScan.EnumerateInstalledApps(new RegistryTreeBackend(reader.Read(probes, 1, cancellationToken).Nodes));
            foreach (var app in apps)
            {
                roots.AddRange(RegistryScan.FindAppRegistryRoots(app, installed));
            }
        }

        var kept = new List<RegistryRoot>();
        foreach (var root in roots)
        {
            if (!roots.Any(other => !ReferenceEquals(other, root) && IsUnder(root, other)) && !kept.Any(other => SameKey(other, root)))
            {
                kept.Add(root);
            }
        }

        return (kept, invalid);
    }

    private static bool SameKey(RegistryRoot left, RegistryRoot right) =>
        string.Equals(left.Hive, right.Hive, StringComparison.Ordinal) && string.Equals(left.View, right.View, StringComparison.Ordinal)
        && string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(RegistryRoot key, RegistryRoot ancestor) =>
        string.Equals(key.Hive, ancestor.Hive, StringComparison.Ordinal) && string.Equals(key.View, ancestor.View, StringComparison.Ordinal)
        && key.Path.StartsWith(ancestor.Path + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>registry/HKLM/SOFTWARE/Vendor.reg</c>, with the view after the hive when the key names one (<c>HKLM (32-bit)</c>).</summary>
    internal static string RegistryRelativePath(RegistryRoot root)
    {
        var hive = root.View is null ? root.Hive : $"{root.Hive} ({root.View}-bit)";
        return $"registry/{hive}/{root.Path.Replace('\\', '/')}.reg";
    }

    private ConfigRecord BuildRegistryRecord(MultiServerPlan plan, RegistryRoot root, string text, int position, OrderedDictionary<string, ConfigRecord> configs, CancellationToken cancellationToken)
    {
        var relative = RegistryRelativePath(root);
        var match = new RegistryExportPlugin().Detect(relative, Encoding.UTF8.GetBytes(text), text)
            ?? throw new InvalidOperationException("A rendered registry export was not recognised.");
        match.Metadata = DetectionMetadata.ValidateDetectionMetadata(match, DetectionCatalog.Default);
        match.Metadata["registry_key"] = root.View is null ? $"{root.Hive}\\{root.Path}" : $"{root.Hive}\\{root.Path},view={root.View}";
        match.Metadata["registry_computer"] = plan.Registry?.Computer;
        var contentType = ContentTypeResolver.FromCatalogFormat(match.Metadata.TryGetValue("catalog_format", out var format) ? format as string : null);
        var configId = ConfigIdentity.Disambiguate(ConfigIdentity.NormaliseConfigId(match, relative), position, configs.ContainsKey);
        var computer = plan.Registry?.Computer;
        return new ConfigRecord
        {
            ConfigId = configId,
            DisplayName = relative,
            FormatId = "registry-export",
            ContentType = contentType,
            Canonical = Canonicaliser.Canonicalise(text, contentType),
            Raw = text,
            Metadata = match.Metadata,
            FileHash = Sha256Hex(text),
            Secrets = ContainsSecret(text, cancellationToken),
            SourcePath = computer is null ? $"{root.Hive}\\{root.Path}" : $"\\\\{computer}\\{root.Hive}\\{root.Path}",
            PluginName = match.PluginName,
            RelativePath = relative,
        };
    }
}
