using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

using Xunit;

namespace DriftBuster.Gui.Tests.ViewModels;

public class RunProfilesViewModelTests
{
    [Fact]
    public void Validation_populates_errors_and_disables_commands_until_valid()
    {
        var service = new FakeDriftbusterService();
        var viewModel = new RunProfilesViewModel(service);

        Assert.Equal("Select a baseline path.", viewModel.Sources[0].Error);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.RunCommand.CanExecute(null));

        viewModel.ProfileName = "sample";

        var baseline = Path.GetTempFileName();

        try
        {
            viewModel.Sources[0].Path = baseline;

            Assert.Null(viewModel.Sources[0].Error);
            Assert.True(viewModel.SaveCommand.CanExecute(null));
            Assert.True(viewModel.RunCommand.CanExecute(null));

            viewModel.AddSourceCommand.Execute(null);

            var second = viewModel.Sources[1];
            Assert.Equal("Select a source path.", second.Error);

            var invalid = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            second.Path = invalid;

            Assert.Equal("Path does not exist.", second.Error);
            Assert.False(viewModel.SaveCommand.CanExecute(null));
            Assert.False(viewModel.RunCommand.CanExecute(null));

            second.Path = string.Empty;
            Assert.Equal("Select a source path.", second.Error);
            Assert.True(viewModel.SaveCommand.CanExecute(null));
            Assert.True(viewModel.RunCommand.CanExecute(null));
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public async Task Run_populates_results_and_enables_open_output_command()
    {
        var output = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var baseline = Path.GetTempFileName();

        try
        {
            var service = new FakeDriftbusterService
            {
                RunProfileHandler = (_, _, _) => Task.FromResult(new RunProfileRunResult
                {
                    OutputDir = output.FullName,
                    Files = new[]
                    {
                        new RunProfileFileResult
                        {
                            Source = "B",
                            Destination = Path.Combine(output.FullName, "b.txt").Replace(Path.DirectorySeparatorChar, '/'),
                            Size = 1,
                            Sha256 = "hash-b",
                        },
                        new RunProfileFileResult
                        {
                            Source = "A",
                            Destination = Path.Combine(output.FullName, "a.txt").Replace(Path.DirectorySeparatorChar, '/'),
                            Size = 2048,
                            Sha256 = "hash-a",
                        },
                    },
                }),
            };

            var viewModel = new RunProfilesViewModel(service)
            {
                ProfileName = "run-test",
            };

            viewModel.Sources[0].Path = baseline;

            await viewModel.RunCommand.ExecuteAsync(null);

            Assert.True(viewModel.HasRunResults);
            Assert.Equal("Run complete. Files copied: 2.", viewModel.StatusMessage);
            var results = viewModel.RunResults.ToList();
            Assert.Equal(2, results.Count);
            Assert.Equal(new[] { "A", "B" }, results.Select(r => r.Source));
            Assert.Equal("2,048 bytes", results[0].Size);
            Assert.Equal("1 byte", results[1].Size);
            Assert.Equal("hash-a", results[0].Hash);
            Assert.True(viewModel.OpenOutputCommand.CanExecute(null));
        }
        finally
        {
            File.Delete(baseline);
            Directory.Delete(output.FullName, recursive: true);
        }
    }
}
