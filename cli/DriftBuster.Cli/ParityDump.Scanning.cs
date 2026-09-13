using System.CommandLine;
using System.Text;

using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Cli;

/// <summary>
/// The <c>hunt</c>, <c>secrets</c> and <c>secrets-context</c> surfaces of <c>parity-dump</c> (py_dump.py
/// <c>cmd_hunt</c>, <c>cmd_secrets</c>, <c>cmd_secrets_context</c>).
/// </summary>
public static partial class ParityDump
{
    private const string RootToken = "<root>";

    /// <summary>py_dump.py <c>SECRET_OPTIONS</c>: one ignore pattern.</summary>
    private static IReadOnlyDictionary<string, object?> SecretOptions()
        => new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["secret_ignore_patterns"] = "PARITY-ALLOW" };

    /// <summary>py_dump.py <c>SECRET_SCANNER</c>: empty, so the packaged ruleset applies.</summary>
    private static IReadOnlyDictionary<string, object?> SecretScannerConfig() => new OrderedDictionary<string, object?>(StringComparer.Ordinal);

    private static Command BuildHunt()
    {
        var pathArgument = new Argument<string>("path") { Description = "File or directory to hunt." };
        var globOption = new Option<string>("--glob") { DefaultValueFactory = _ => "**/*", Description = "Traversal glob." };
        var excludeOption = new Option<string[]>("--exclude") { Description = "Exclusion pattern (repeatable)." };
        var hunt = new Command("hunt", "hunt_path JSON hits, one per line.");
        hunt.Arguments.Add(pathArgument);
        hunt.Options.Add(globOption);
        hunt.Options.Add(excludeOption);
        hunt.SetAction(parseResult =>
        {
            WriteLines(parseResult, Hunt(parseResult.GetValue(pathArgument)!, parseResult.GetValue(globOption)!, parseResult.GetValue(excludeOption) ?? []));
            return 0;
        });
        return hunt;
    }

    private static Command BuildSecrets()
    {
        var pathArgument = new Argument<string>("path") { Description = "File or directory to copy through the secret filter." };
        var rulesetOption = new Option<string?>("--ruleset") { Description = "JSON ruleset used as secret_scanner['ruleset']." };
        var secrets = new Command("secrets", "copy_with_secret_filter results per file.");
        secrets.Arguments.Add(pathArgument);
        secrets.Options.Add(rulesetOption);
        secrets.SetAction(parseResult =>
        {
            WriteLines(parseResult, Secrets(parseResult.GetValue(pathArgument)!, parseResult.GetValue(rulesetOption)));
            return 0;
        });
        return secrets;
    }

    private static Command BuildSecretsContext()
    {
        var pathArgument = new Argument<string>("path") { Description = "Context JSON file or directory of them." };
        var command = new Command("secrets-context", "build_context and manifest_secret_scanner per config file.");
        command.Arguments.Add(pathArgument);
        command.SetAction(parseResult =>
        {
            WriteLines(parseResult, SecretsContext(parseResult.GetValue(pathArgument)!));
            return 0;
        });
        return command;
    }

    internal static IEnumerable<string> Hunt(string root, string glob, IReadOnlyList<string> excludes)
    {
        var prefix = PythonPurePath.Str(root);
        var result = HuntEngine.HuntPath(root, HuntRules.Default, glob, excludePatterns: excludes.Count > 0 ? excludes : null);
        foreach (var entry in HuntEngine.ToJson(result))
        {
            var path = (string)entry["path"]!;
            if (string.Equals(path, prefix, StringComparison.Ordinal))
            {
                entry["path"] = RootToken;
            }
            else if (path.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                entry["path"] = RootToken + path[prefix.Length..];
            }

            yield return CanonicalJson.Serialize(entry);
        }

        if (result.UnreadableFiles.Count > 0)
        {
            // Fix b: Python aborts the hunt instead; any such record fails the compare.
            yield return CanonicalJson.Serialize(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["unreadable_files"] = result.UnreadableFiles.Cast<object?>().ToList(),
            });
        }
    }

    internal static IEnumerable<string> Secrets(string root, string? rulesetPath = null)
    {
        var scanner = SecretScannerConfig();
        if (rulesetPath is not null)
        {
            if (!PythonJson.TryLoads(File.ReadAllText(rulesetPath, Encoding.UTF8), out var ruleset))
            {
                throw new InvalidDataException($"not JSON: {rulesetPath}");
            }

            scanner = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["ruleset"] = ruleset };
        }

        foreach (var (relative, full, errored) in Walk(root))
        {
            yield return CanonicalJson.Serialize(errored ? ErrorRecord(relative, "DetectorIOError") : SecretRecord(relative, full, scanner));
        }
    }

    // py_dump.py _secret_copy, plus "redaction_guard" when fix g stopped a rule (Python never returns for that file).
    private static OrderedDictionary<string, object?> SecretRecord(string relative, string full, IReadOnlyDictionary<string, object?> scanner)
    {
        var context = SecretScanner.BuildContext(SecretOptions(), scanner);
        var log = new List<object?>();
        var tmp = Directory.CreateTempSubdirectory("driftbuster-parity-secrets-");
        try
        {
            var destination = Path.Combine(tmp.FullName, "out", PathText.Name(full));
            var (size, digest) = SecretScanner.CopyWithSecretFilter(full, destination, relative, context, log.Add);
            var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = relative,
                ["size"] = size,
                ["sha256"] = digest,
                ["findings"] = context.Findings
                    .Select(finding => (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["rule"] = finding.Rule,
                        ["line"] = finding.Line,
                        ["snippet"] = finding.Snippet,
                    })
                    .ToList(),
                ["log"] = log,
                ["output_text"] = ReadReplace(destination),
            };
            if (context.RedactionGuards.Count > 0)
            {
                record["redaction_guard"] = context.RedactionGuards
                    .Select(guard => (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["rule"] = guard.Rule, ["line"] = guard.Line })
                    .ToList();
            }

            return record;
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    internal static IEnumerable<string> SecretsContext(string root)
    {
        var files = Directory.Exists(root)
            ? Directory.GetFiles(root, "*.json").Where(PythonPath.IsFile).Order(Comparer<string>.Create(PathText.CompareCodePoints)).ToList()
            : [root];
        foreach (var file in files)
        {
            if (!PythonJson.TryLoads(File.ReadAllText(file, Encoding.UTF8), out var config) || config is not IReadOnlyDictionary<string, object?> mapping)
            {
                throw new InvalidDataException($"not a JSON object: {file}");
            }

            var options = mapping.GetValueOrDefault("options") as IReadOnlyDictionary<string, object?>;
            var scanner = mapping.GetValueOrDefault("secret_scanner") as IReadOnlyDictionary<string, object?>;
            SecretScanner.ResetSecretRuleCache();
            var context = SecretScanner.BuildContext(options, scanner);
            var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = PathText.Name(file),
                ["context"] = ContextPayload(context),
            };
            if (options is not null && scanner is not null)
            {
                record["manifest"] = SecretScanner.ManifestSecretScanner(options, scanner, context);
            }

            yield return CanonicalJson.Serialize(record);
        }
    }

    private static OrderedDictionary<string, object?> ContextPayload(SecretDetectionContext context)
    {
        var ignoreRules = context.IgnoreRules.ToList();
        ignoreRules.Sort(PathText.CompareCodePoints);
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rules"] = context.Rules
                .Select(rule => (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = rule.Name,
                    ["pattern"] = rule.Pattern.Pattern,
                    ["flags"] = (int)rule.Pattern.Flags,
                    ["description"] = rule.Description,
                })
                .ToList(),
            ["version"] = context.Version,
            ["ignore_rules"] = ignoreRules.Cast<object?>().ToList(),
            ["ignore_patterns"] = context.IgnorePatterns.Select(pattern => (object?)pattern.Pattern).ToList(),
            ["ignore_pattern_text"] = context.IgnorePatternText.Cast<object?>().ToList(),
            ["rules_loaded"] = context.RulesLoaded,
        };
    }
}
