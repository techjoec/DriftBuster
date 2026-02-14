using Xunit;

namespace DriftBuster.Gui.Tests.Backend;

[CollectionDefinition("BackendTests", DisableParallelization = true)]
public sealed class BackendTestCollection : ICollectionFixture<BackendDataRootFixture>
{
}
