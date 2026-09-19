using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;
using DriftBuster.Backend.Settings;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The script plugin (PowerShell, batch, CMD, VBScript) and the script settings reader.</summary>
public sealed class ScriptPluginTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-script-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private const string PowerShell = """
        #Requires -Version 5.1
        param(
            [string]$Environment = 'prod'
        )
        $ServerUrl = "https://api.contoso.local"
        $env:APP_MODE = 'release'
        function Invoke-Deploy { Write-Host "Deploying" }
        Invoke-Deploy
        if ($Retries -eq 3) { }
        """;

    private const string Batch = """
        @echo off
        setlocal
        rem configure
        set APP_HOME=C:\Apps\Contoso
        set "LOG_LEVEL=info"
        set /a COUNT=1+1
        goto :run
        :run
        exit /b 0
        """;

    private const string VbScript = """
        Option Explicit
        Dim shell, server
        Const Timeout = 30 ' seconds
        Private Const Mode = "fast"
        server = "db01.contoso.local"
        Set shell = CreateObject("WScript.Shell")
        WScript.Echo server
        """;

    private static DetectionMatch? Detect(string name, string content)
        => new ScriptPlugin().Detect(name, Encoding.UTF8.GetBytes(content), content);

    private static Dictionary<string, string> Settings(string content)
        => SettingsExtractor.Extract("script-config", "script", content, "h").Entries
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    [Theory]
    [InlineData("deploy.ps1", PowerShell, "ps1-shell", "PowerShell")]
    [InlineData("start.bat", Batch, "batch-script", "batch")]
    [InlineData("start.cmd", Batch, "cmd-shell", "batch")]
    [InlineData("check.vbs", VbScript, "vbscript", "VBScript")]
    public void EachLanguageIsItsVariant(string name, string content, string variant, string language)
    {
        var match = Detect(name, content);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("script-config");
        match.Variant.Should().Be(variant);
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Metadata!["script_language"].Should().Be(language);
        match.Reasons[^1].Should().StartWith("File extension");
    }

    [Fact]
    public void WithoutAScriptExtensionThreeSignalsAreNeeded()
    {
        const string two = "@echo off\nset A=1\n";
        Detect("start.bat", two)!.Confidence.Should().BeApproximately(0.85, 1e-9);
        Detect("notes.txt", two).Should().BeNull();
        Detect("notes.txt", Batch)!.Variant.Should().Be("batch-script");
    }

    [Fact]
    public void ProseWithASnippetIsNotClaimed()
    {
        const string readme = "# Deploy\n\nRun this:\n\n$ServerUrl = 'https://x'\nInvoke-Deploy -Url $ServerUrl\n";
        Detect("README.md", readme).Should().BeNull();
    }

    [Fact]
    public void BinarySamplesAreSkipped()
    {
        new ScriptPlugin().Detect("a.ps1", [0, 1, 2], text: null).Should().BeNull();
    }

    [Fact]
    public void AScriptEmbeddingXmlIsStillAScript()
    {
        var path = Path.Combine(_tmp.FullName, "package.ps1");
        File.WriteAllText(path, "param([string]$Version = '1.0')\n$manifest = @\"\n<?xml version=\"1.0\"?>\n<Package><Identity Name=\"x\" /></Package>\n\"@\nSet-Content -Path out.xml -Value $manifest\n");

        var match = Detector.ScanFileWithDefaults(path);

        match!.PluginName.Should().Be("script");
        match.Metadata!["catalog_variant"].Should().Be("ps1-shell");
    }

    [Fact]
    public void PowerShellSettingsAreTheAssignedVariables()
    {
        Settings(PowerShell).Should().Equal(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["$Environment"] = "prod",
            ["$ServerUrl"] = "https://api.contoso.local",
            ["$env:APP_MODE"] = "release",
        });
    }

    [Fact]
    public void BatchSettingsAreSetStatementsWithoutArithmeticOrPrompts()
    {
        Settings(Batch).Should().Equal(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["APP_HOME"] = @"C:\Apps\Contoso",
            ["LOG_LEVEL"] = "info",
        });
    }

    [Fact]
    public void VbScriptSettingsAreConstantsAndAssignmentsWithoutComments()
    {
        Settings(VbScript).Should().Equal(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Const Timeout"] = "30",
            ["Const Mode"] = "fast",
            ["server"] = "db01.contoso.local",
        });
    }

    [Fact]
    public void AScriptThatAssignsNothingFallsBackToTheLineReader()
    {
        SettingsExtractor.Extract("script-config", "script", "@echo off\ncall run.cmd\n", "h").Mode.Should().Be(SettingsMode.Lines);
    }
}
