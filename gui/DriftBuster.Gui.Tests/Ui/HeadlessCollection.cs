using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

public static class HeadlessCollection
{
    public const string Name = "AvaloniaHeadlessCollection";
}

[CollectionDefinition(HeadlessCollection.Name)]
public sealed class HeadlessCollectionDefinition : ICollectionFixture<HeadlessFixture>
{
}
