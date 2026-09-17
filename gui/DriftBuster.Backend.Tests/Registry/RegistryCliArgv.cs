using System.Globalization;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// The <c>registry_cli.build_parser</c> argv shapes the oracle runs use, dispatched to <see cref="RegistryCommands"/> as <c>main</c>
/// dispatches them: repeatable <c>--keyword</c>, <c>--pattern</c>, <c>--root</c> and <c>--remote-target</c>, <c>--alias</c>, and
/// <c>--max-depth</c>/<c>--max-hits</c> (int) and <c>--time-budget</c> (float). Returns the printed lines.
/// </summary>
internal static class RegistryCliArgv
{
    public static IReadOnlyList<string> Run(IReadOnlyList<string> argv)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (var index = 1; index < argv.Count; index++)
        {
            if (argv[index].StartsWith("--", StringComparison.Ordinal))
            {
                if (!options.TryGetValue(argv[index], out var values))
                {
                    values = [];
                    options[argv[index]] = values;
                }

                values.Add(argv[++index]);
            }
            else
            {
                positional.Add(argv[index]);
            }
        }

        List<string> Many(string name) => options.GetValueOrDefault(name, []);
        System.Numerics.BigInteger? Int(string name) => options.TryGetValue(name, out var values) ? PythonBuiltins.Int(values[^1]) : null;
        double Float(string name) => options.TryGetValue(name, out var values)
            ? (string.Equals(values[^1], "inf", StringComparison.Ordinal) ? double.PositiveInfinity : double.Parse(values[^1], CultureInfo.InvariantCulture))
            : 10.0;

        switch (argv[0])
        {
            case "list-apps":
                return RegistryCommands.ListApps();
            case "suggest-roots":
                return RegistryCommands.SuggestRoots(positional[0]);
            case "search":
                return RegistryCommands.Search(
                    positional[0], Many("--keyword"), Many("--pattern"), Int("--max-depth"), Int("--max-hits"), Float("--time-budget"), Many("--root"));
            default:
                var snippet = RegistryCommands.EmitConfig(
                    positional[0],
                    options.TryGetValue("--alias", out var alias) ? alias[^1] : null,
                    Many("--keyword"),
                    Many("--pattern"),
                    Int("--max-depth"),
                    Int("--max-hits"),
                    Float("--time-budget"),
                    Many("--remote-target"),
                    Many("--root"));
                return [RegistryCommands.EmitConfigJson(snippet)];
        }
    }
}
