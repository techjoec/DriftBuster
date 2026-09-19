using DriftBuster.Backend.Curation;

namespace DriftBuster.Backend.Tests.Curation;

/// <summary>The curation file: missing, saved and loaded, damaged, and merged on import.</summary>
public sealed class CurationStoreTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-curation-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string PathOf(string name) => Path.Combine(_tmp.FullName, name);

    [Fact]
    public void A_missing_file_is_an_empty_document()
    {
        var document = CurationStore.Load(PathOf("none.json"));

        document.Groups.Should().BeEmpty();
        document.SchemaVersion.Should().Be(CurationDocument.CurrentSchemaVersion);
    }

    [Fact]
    public void Saves_and_loads_everything()
    {
        var path = PathOf("sub/curation.json");
        var document = new CurationDocument
        {
            Groups = [new CurationGroup { Name = "Caching", Members = [new CurationTarget { File = "a.json", Key = "Cache.*" }] }],
            Rules = [new CurationRule { Name = "stamps", KeyPattern = "Built", Ignore = true, Mask = CurationChoiceKinds.Mask, Groups = ["Noise"] }],
            Choices = [new CurationChoice { Target = new CurationTarget { Key = "x", ValueHash = "abc" }, Kind = CurationChoiceKinds.Ignore, Scope = "hosts-1" }],
            Review = [new CurationReviewItem { Target = new CurationTarget { File = "a.json", Key = "k" }, Note = "check" }],
        };

        CurationStore.Save(document, path);
        var loaded = CurationStore.Load(path);

        loaded.Groups.Single().Members.Single().Should().Be(document.Groups[0].Members[0]);
        loaded.Rules.Single().Should().BeEquivalentTo(document.Rules[0]);
        loaded.Choices.Single().Should().Be(document.Choices[0]);
        loaded.Review.Single().Note.Should().Be("check");
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void A_damaged_file_raises_instead_of_being_replaced()
    {
        var path = PathOf("bad.json");
        File.WriteAllText(path, "{ not json");

        var load = () => CurationStore.Load(path);

        load.Should().Throw<InvalidDataException>().WithMessage(path + ": $*");
        File.ReadAllText(path).Should().Be("{ not json");
    }

    [Fact]
    public void Members_left_out_of_a_hand_edited_file_take_their_defaults()
    {
        var path = PathOf("partial.json");
        File.WriteAllText(path, """{"schema_version": 1, "groups": [{"name": "g"}], "rules": [{"name": "r", "enabled": true}]}""");

        var document = CurationStore.Load(path);

        document.Groups.Single().Members.Should().BeEmpty();
        document.Rules.Single().Should().BeEquivalentTo(new CurationRule { Name = "r" });
        document.Choices.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"schema_version": 1, "rules": null}""", "$.rules")]
    [InlineData("""{"schema_version": 1, "labels": []}""", "$")]
    [InlineData("""{"schema_version": 1, "groups": [], "groups": []}""", "$")]
    [InlineData("""{"schema_version": 1, "rules": [{"name": "r"}]}""", "$.rules[0]")]
    [InlineData("""{"schema_version": 2}""", "$.schema_version")]
    [InlineData("""{}""", "$.schema_version: 0")]
    public void A_file_that_does_not_fit_is_refused_with_its_json_path(string text, string where)
    {
        var path = PathOf("strict.json");
        File.WriteAllText(path, text);

        var load = () => CurationStore.Load(path);

        load.Should().Throw<InvalidDataException>().Which.Message.Should().StartWith(path + ": " + where);
    }

    [Fact]
    public void Merge_adds_what_is_new_and_joins_group_members()
    {
        var shared = new CurationTarget { Key = "a" };
        var current = new CurationDocument
        {
            Groups = [new CurationGroup { Name = "G", Members = [shared] }],
            Rules = [new CurationRule { Name = "R" }],
            Choices = [new CurationChoice { Target = shared }],
            Review = [new CurationReviewItem { Target = shared }],
        };
        var incoming = new CurationDocument
        {
            Groups = [new CurationGroup { Name = "g", Members = [shared, new CurationTarget { Key = "b" }] }, new CurationGroup { Name = "H" }],
            Rules = [new CurationRule { Name = "r", Ignore = true }, new CurationRule { Name = "S" }],
            Choices = [new CurationChoice { Target = shared }, new CurationChoice { Target = shared, Kind = CurationChoiceKinds.Mask }],
            Review = [new CurationReviewItem { Target = shared }, new CurationReviewItem { Target = new CurationTarget { Key = "b" } }],
        };

        var merged = CurationStore.Merge(current, incoming);

        merged.Groups.Select(group => group.Name).Should().Equal("G", "H");
        merged.Groups[0].Members.Select(member => member.Key).Should().Equal("a", "b");
        merged.Rules.Select(rule => rule.Name).Should().Equal("R", "S");
        merged.Rules[0].Ignore.Should().BeFalse("an existing rule is kept as it is");
        merged.Choices.Select(choice => choice.Kind).Should().Equal(CurationChoiceKinds.Ignore, CurationChoiceKinds.Mask);
        merged.Review.Should().HaveCount(2);
    }
}
