using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.Tests.Fakes;

/// <summary>Result records for fakes and tests.</summary>
internal static class Results
{
    public static RunProfileRunResult Run(RunProfileDefinition? profile = null, string outputDir = "", params RunProfileFileResult[] files)
    {
        profile ??= new RunProfileDefinition { Name = "profile" };
        return new RunProfileRunResult(
            profile,
            "20250101T000000Z",
            outputDir,
            profile.Sources.Count > 0 ? profile.Sources[0].Path : string.Empty,
            [],
            files,
            new SecretRunSummary("test", RulesLoaded: false, [], [], [], [], []));
    }

    public static OfflineCollectorResult Collector(string packagePath = "", string configFileName = "", string scriptFileName = "")
        => new(packagePath, configFileName, scriptFileName);
}
