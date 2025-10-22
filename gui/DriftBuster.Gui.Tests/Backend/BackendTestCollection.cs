using System;
using System.IO;
using Xunit;

namespace DriftBuster.Gui.Tests.Backend;

[CollectionDefinition("BackendTests", DisableParallelization = true)]
public sealed class BackendTestCollection : ICollectionFixture<BackendDataRootFixture>
{
}

public sealed class BackendDataRootFixture : IDisposable
{
    private readonly string _root;

    public BackendDataRootFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), "DriftbusterTests", "DataRoot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("DRIFTBUSTER_DATA_ROOT", _root);
    }

    public string Root => _root;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIFTBUSTER_DATA_ROOT", null);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
