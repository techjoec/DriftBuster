using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

[Collection(EnvironmentVariablesCollection.Name)]
public sealed class RealEntryPointIntegrationTests
{
    [Fact]
    public async Task RunDiffCommand_uses_real_backend_for_multiple_comparisons()
    {
        using var temp = new TempDirectory();
        var baseline = CreateFile(temp.Path, "baseline.txt", "alpha\n");
        var comparisonA = CreateFile(temp.Path, "comparison-a.txt", "alpha\nbeta\n");
        var comparisonB = CreateFile(temp.Path, "comparison-b.txt", "alpha\ngamma\n");

        var viewModel = new DiffViewModel(
            new DriftbusterService(),
            new DiffPlannerMruStore(temp.Path));

        await viewModel.Initialization;
        viewModel.Inputs[0].Path = baseline;
        viewModel.Inputs[1].Path = comparisonA;
        viewModel.AddVersionCommand.Execute(null);
        viewModel.Inputs[2].Path = comparisonB;

        await viewModel.RunDiffCommand.ExecuteAsync(null);

        viewModel.HasError.Should().BeFalse();
        viewModel.Comparisons.Should().HaveCount(2);
        viewModel.HasSanitizedJson.Should().BeTrue();
        viewModel.JsonViewMode.Should().Be(DiffViewModel.DiffJsonViewMode.Sanitized);
    }

    [Fact]
    public async Task RunAllCommand_uses_real_multi_server_entrypoint()
    {
        using var tempDataRoot = new TempDirectory();
        using var dataRootEnv = new EnvironmentVariableScope("DRIFTBUSTER_DATA_ROOT", tempDataRoot.Path);

        var repositoryRoot = ResolveRepositoryRoot();
        var server01 = Path.Combine(repositoryRoot, "fixtures", "multi-server", "server01");
        var server02 = Path.Combine(repositoryRoot, "fixtures", "multi-server", "server02");

        Directory.Exists(server01).Should().BeTrue();
        Directory.Exists(server02).Should().BeTrue();

        var viewModel = new ServerSelectionViewModel(
            new DriftbusterService(),
            new ToastService(action => action()),
            cacheService: new InMemorySessionCacheService());

        foreach (var slot in viewModel.Servers)
        {
            slot.IsEnabled = false;
        }

        ConfigureSlot(viewModel.Servers[0], "Baseline", server01);
        ConfigureSlot(viewModel.Servers[1], "Drift", server02);

        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.IsBusy.Should().BeFalse();
        viewModel.CatalogViewModel.HasEntries.Should().BeTrue();
        viewModel.FilteredActivityEntries.Should().NotBeEmpty();
        viewModel.Servers
            .Where(slot => slot.IsEnabled)
            .Select(slot => slot.RunState)
            .Should()
            .OnlyContain(state => state == ServerScanStatus.Succeeded || state == ServerScanStatus.Cached);
    }

    [Fact]
    public async Task RunMissingCommand_reuses_cached_results_after_real_run()
    {
        using var tempDataRoot = new TempDirectory();
        using var dataRootEnv = new EnvironmentVariableScope("DRIFTBUSTER_DATA_ROOT", tempDataRoot.Path);

        var repositoryRoot = ResolveRepositoryRoot();
        var server01 = Path.Combine(repositoryRoot, "fixtures", "multi-server", "server01");
        var server02 = Path.Combine(repositoryRoot, "fixtures", "multi-server", "server02");

        var viewModel = new ServerSelectionViewModel(
            new DriftbusterService(),
            new ToastService(action => action()),
            cacheService: new InMemorySessionCacheService());

        foreach (var slot in viewModel.Servers)
        {
            slot.IsEnabled = false;
        }

        ConfigureSlot(viewModel.Servers[0], "Baseline", server01);
        ConfigureSlot(viewModel.Servers[1], "Drift", server02);

        await viewModel.RunAllCommand.ExecuteAsync(null);
        await viewModel.RunMissingCommand.ExecuteAsync(null);

        viewModel.Servers
            .Where(slot => slot.IsEnabled)
            .Select(slot => slot.RunState)
            .Should()
            .OnlyContain(state => state == ServerScanStatus.Cached);
    }

    private static void ConfigureSlot(ServerSlotViewModel slot, string label, string rootPath)
    {
        slot.Label = label;
        slot.IsEnabled = true;
        slot.Scope = ServerScanScope.CustomRoots;
        slot.ReplaceRoots(new[]
        {
            new RootEntryViewModel(rootPath),
        });
    }

    private static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "pyproject.toml")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test host.");
    }

    private static string CreateFile(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DriftBusterTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
        }
    }
}
