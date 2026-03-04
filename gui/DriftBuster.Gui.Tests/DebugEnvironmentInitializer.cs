using System;
using System.Runtime.CompilerServices;

namespace DriftBuster.Gui.Tests;

internal static class DebugEnvironmentInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("DRIFTBUSTER_DEBUG", "1");
    }
}
