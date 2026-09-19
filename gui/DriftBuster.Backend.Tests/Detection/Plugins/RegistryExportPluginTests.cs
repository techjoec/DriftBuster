using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;
using DriftBuster.Backend.Settings;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The registry-export plugin and the .reg reader behind it.</summary>
public sealed class RegistryExportPluginTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-reg-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private const string Export = """
        Windows Registry Editor Version 5.00

        ; exported for review
        [HKEY_LOCAL_MACHINE\SOFTWARE\Contoso\App]
        "InstallDir"="C:\\Program Files\\Contoso"
        "Level"=dword:0000002a
        @="default text"
        "Quote \"name\""="say \"hi\""
        "url:thing"="http://x"
        "Path"=hex(2):25,00,53,00,79,00,73,00,74,00,65,00,6d,00,52,00,6f,00,6f,00,74,00,\
          25,00,00,00
        "Hosts"=hex(7):61,00,00,00,62,00,00,00,00,00
        "Blob"=hex:01,02,\
          03
        "Gone"=-

        [-HKEY_CURRENT_USER\Software\Contoso\Old]
        """;

    // regedit writes version 5 exports as UTF-16LE with a byte order mark.
    private static byte[] Utf16(string text) => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];

    [Fact]
    public void Utf16ExportIsDetectedFromItsByteOrderMark()
    {
        var match = new RegistryExportPlugin().Detect("app.reg", Utf16(Export), text: null);

        match.Should().NotBeNull();
        match!.FormatName.Should().Be("registry-export");
        match.Variant.Should().Be("regedit5");
        match.Confidence.Should().BeApproximately(0.95, 1e-9);
        match.Reasons.Should().Equal(
            "Found Registry Editor export header (version 5.00)",
            "Found registry key sections under HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE",
            "File extension .reg suggests a Registry Editor export",
            "Export deletes registry keys or values when imported");
        match.Metadata!["registry_editor_version"].Should().Be("5.00");
        YamlPluginTests.Strings(match.Metadata["hives"]).Should().Equal("HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE");
        match.Metadata["key_count"].Should().Be(2L);
        match.Metadata["value_count"].Should().Be(9L);
        match.Metadata["deleted_keys"].Should().Be(1L);
        match.Metadata["deleted_values"].Should().Be(1L);
    }

    [Fact]
    public void Regedit4ExportWithoutTheExtensionStillMatches()
    {
        const string text = "REGEDIT4\n\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\X]\n\"A\"=\"1\"\n";
        var match = new RegistryExportPlugin().Detect("settings.txt", Encoding.ASCII.GetBytes(text), text);

        match!.Variant.Should().Be("regedit4");
        match.Confidence.Should().BeApproximately(0.85, 1e-9);
        match.Metadata!.ContainsKey("deleted_keys").Should().BeFalse();
    }

    [Fact]
    public void HeaderAloneIsTheWeakestMatch()
    {
        const string text = "Windows Registry Editor Version 5.00\n";
        var match = new RegistryExportPlugin().Detect("empty.dat", Encoding.ASCII.GetBytes(text), text);

        match!.Confidence.Should().BeApproximately(0.7, 1e-9);
        match.Metadata!["key_count"].Should().Be(0L);
    }

    [Theory]
    [InlineData("[HKEY_LOCAL_MACHINE\\SOFTWARE\\X]\n\"A\"=\"1\"\n")]
    [InlineData("; Windows Registry Editor Version 5.00\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\X]\n")]
    [InlineData("")]
    public void AnExtensionWithoutTheHeaderIsNotAnExport(string text)
    {
        new RegistryExportPlugin().Detect("app.reg", Encoding.ASCII.GetBytes(text), text).Should().BeNull();
    }

    [Fact]
    public void BinaryWithoutAByteOrderMarkIsNotAnExport()
    {
        new RegistryExportPlugin().Detect("app.reg", [0x00, 0x01, 0x02], text: null).Should().BeNull();
    }

    [Fact]
    public void DetectorFindsAUtf16ExportOnDisk()
    {
        var path = Path.Combine(_tmp.FullName, "app.reg");
        File.WriteAllBytes(path, Utf16(Export));

        var match = Detector.ScanFileWithDefaults(path);

        match!.PluginName.Should().Be("registry-export");
        match.Metadata!["catalog_format"].Should().Be("registry-export");
        match.Metadata["catalog_variant"].Should().Be("regedit5");
    }

    [Fact]
    public void SettingsNameEachValueByKeyPathAndShowReadableData()
    {
        var settings = SettingsExtractor.Extract("registry-export", "registry-export", Export, "h").Entries
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        const string key = @"[HKEY_LOCAL_MACHINE\SOFTWARE\Contoso\App] ";
        settings.Should().Contain(key + "InstallDir", @"C:\Program Files\Contoso")
            .And.Contain(key + "Level", "dword:0000002a (42)")
            .And.Contain(key + "(Default)", "default text")
            .And.Contain(key + "Quote \"name\"", "say \"hi\"")
            .And.Contain(key + "url:thing", "http://x")
            .And.Contain(key + "Path", "%SystemRoot%")
            .And.Contain(key + "Hosts", "a\nb")
            .And.Contain(key + "Blob", "hex:01,02,03")
            .And.Contain(key + "Gone", "(deleted)")
            .And.Contain(@"[HKEY_CURRENT_USER\Software\Contoso\Old] (key)", "(deleted)");
        settings.Should().HaveCount(10);
    }

    [Fact]
    public void Regedit4HexStringsAreLatin1()
    {
        const string text = "REGEDIT4\n\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\X]\n\"P\"=hex(2):25,54,4d,50,25,00\n\"Q\"=hex(7):61,00,62,00,00\n";
        var settings = SettingsExtractor.Extract("registry-export", "registry-export", text, "h").Entries;

        settings.Select(entry => entry.Value).Should().Equal("%TMP%", "a\nb");
    }

    [Theory]
    [InlineData("dword:zz", "dword:zz")]
    [InlineData("hex(2):zz", "hex(2):zz")]
    [InlineData("bare", "bare")]
    [InlineData("hex(b):00, 01", "hex(b):00,01")]
    public void UnusualDataIsShownAsWritten(string raw, string shown)
    {
        RegistryExportText.RenderData(raw, unicode: true).Should().Be(shown);
    }

    [Fact]
    public void ValuesBeforeAnySectionAndMalformedLinesAreSkipped()
    {
        const string text = "Windows Registry Editor Version 5.00\n\"Orphan\"=\"x\"\n[HKEY_LOCAL_MACHINE\\S]\n\"Open=\"x\"\n\"NoEquals\" \"x\"\nplain line\n\"Kept\"=\"y\"\n";

        var settings = SettingsExtractor.Extract("registry-export", "registry-export", text, "h").Entries;

        settings.Select(entry => entry.Key).Should().Equal(@"[HKEY_LOCAL_MACHINE\S] Kept");
    }

    [Fact]
    public void TextWithoutTheHeaderFallsBackToTheLineReader()
    {
        var settings = SettingsExtractor.Extract("registry-export", "registry-export", "[HKEY_LOCAL_MACHINE\\S]\n\"A\"=\"1\"\n", "h");

        settings.Mode.Should().Be(SettingsMode.Lines);
    }
}
