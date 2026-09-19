using System.Text.Json;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

/// <summary><c>versions.json</c>: the version of each shipped component. Read strictly; every key is required.</summary>
internal sealed record ComponentVersions(string Core, string Catalog, string Gui, string Powershell)
{
    public static ComponentVersions Load(string root)
    {
        var path = Path.Combine(root, "versions.json");
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, CliJsonContext.Default.ComponentVersions)
                ?? throw new CommandExitException($"{path}: the file holds null.");
        }
        catch (JsonException exc)
        {
            throw new CommandExitException($"{path}: {exc.Path ?? "$"}: {exc.Message}", exc);
        }
        catch (FileNotFoundException exc)
        {
            throw new CommandExitException($"{path}: not found.", exc);
        }
    }
}
