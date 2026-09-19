using DriftBuster.Backend.Curation;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Tests.Services;

/// <summary>The file-backed curation service: saving, a damaged file, import and export, and the scan history.</summary>
public sealed class CurationServiceTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-curation-service-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string PathOf(string name) => Path.Combine(_tmp.FullName, name);

    [Fact]
    public void Changes_are_saved_and_announced()
    {
        var service = new CurationService(PathOf("curation.json"), PathOf("history.db"));
        var changes = 0;
        service.Changed += (_, _) => changes++;

        service.Update(document => document with { Groups = [new CurationGroup { Name = "g" }] });

        changes.Should().Be(1);
        CurationStore.Load(PathOf("curation.json")).Groups.Single().Name.Should().Be("g");
        service.LoadError.Should().BeNull();
    }

    [Fact]
    public void A_damaged_file_is_left_alone()
    {
        File.WriteAllText(PathOf("curation.json"), "{ broken");
        var service = new CurationService(PathOf("curation.json"), PathOf("history.db"));

        service.Update(document => document with { Groups = [new CurationGroup { Name = "g" }] });

        service.LoadError.Should().Contain("could not be read");
        service.Document.Groups.Should().ContainSingle("changes are kept in memory");
        File.ReadAllText(PathOf("curation.json")).Should().Be("{ broken");
    }

    [Fact]
    public void Export_and_import_merge_or_replace()
    {
        var service = new CurationService(PathOf("curation.json"), PathOf("history.db"));
        service.Update(document => document with { Groups = [new CurationGroup { Name = "mine" }] });
        CurationStore.Save(new CurationDocument { Groups = [new CurationGroup { Name = "theirs" }] }, PathOf("incoming.json"));

        service.Export(PathOf("exported.json"));
        service.Import(PathOf("incoming.json"), replace: false);
        service.Document.Groups.Select(group => group.Name).Should().Equal("mine", "theirs");
        service.Import(PathOf("incoming.json"), replace: true);
        service.Document.Groups.Select(group => group.Name).Should().Equal("theirs");

        CurationStore.Load(PathOf("exported.json")).Groups.Single().Name.Should().Be("mine");
    }

    [Fact]
    public async Task Scans_are_recorded_and_queried()
    {
        var service = new CurationService(PathOf("curation.json"), PathOf("history.db"));

        await service.RecordHistoryAsync(Fakes.SampleComparison.Build(), "hosts-1");

        service.HistoryStats().Runs.Should().Be(1);
        service.SettingHistory("inetpub/app/appsettings.json", "Cache.Minutes").Should().HaveCount(3);
        service.WhereSettingIsSet("Cache.Minutes").Should().HaveCount(3);
        service.WhereValueAppears(CurationTarget.ValueHashOf("two")).Should().ContainSingle().Which.Value.Should().BeNull("masked values are kept as fingerprints");
        service.ClearHistory();
        service.HistoryStats().Runs.Should().Be(0);
    }
}
