using System;
using System.IO;

namespace DriftBuster.Gui.Tests.Backend;

public sealed class BackendDataRootFixture : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable("DRIFTBUSTER_DATA_ROOT");

    public BackendDataRootFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), "DriftbusterTests", "DataRoot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("DRIFTBUSTER_DATA_ROOT", _root);
    }

    public string Root => _root;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIFTBUSTER_DATA_ROOT", _previousRoot);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
