using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

[CollectionDefinition(HeadlessCollection.Name)]
public sealed class HeadlessCollectionDefinition : ICollectionFixture<HeadlessFixture>
{
}
