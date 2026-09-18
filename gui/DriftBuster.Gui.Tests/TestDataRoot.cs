using System.Runtime.CompilerServices;

namespace DriftBuster.Gui.Tests;

/// <summary>
/// Points the data root at a per-run temporary folder before any test runs, so view models that default to the data root
/// (sessions, exports, logs) never write into the developer's own profile. Removed when the test process exits.
/// </summary>
internal static class TestDataRoot
{
    [ModuleInitializer]
    internal static void Initialise()
    {
        var root = Path.Combine(Path.GetTempPath(), "DriftbusterTests", "GuiDataRoot", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DRIFTBUSTER_DATA_ROOT", root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
    }
}
