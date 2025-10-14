using System.IO;
using System.Threading.Tasks;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

using Xunit;

namespace DriftBuster.Gui.Tests.ViewModels;

public class HuntViewModelTests
{
    [Fact]
    public async Task RunHuntCommand_populates_hits_and_status()
    {
        var service = new FakeDriftbusterService
        {
            HuntResponse = new HuntResult
            {
                Directory = "configs",
                Pattern = "server",
                Count = 1,
                Hits = new[]
                {
                    new HuntHit
                    {
                        Rule = new HuntRuleSummary
                        {
                            Name = "server-name",
                            Description = "Detect server names",
                            TokenName = "server_name",
                        },
                        RelativePath = "configs/app.txt",
                        Path = "/configs/app.txt",
                        LineNumber = 4,
                        Excerpt = "server: host",
                    },
                },
                RawJson = "{\"count\":1}",
            },
        };

        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var viewModel = new HuntViewModel(service)
            {
                DirectoryPath = directory.FullName,
                Pattern = "server",
            };

            Assert.True(viewModel.RunHuntCommand.CanExecute(null));
            Assert.True(viewModel.HasNoHits);

            await viewModel.RunHuntCommand.ExecuteAsync(null);

            Assert.Equal(1, viewModel.ResultCount);
            Assert.True(viewModel.HasHits);
            Assert.False(viewModel.HasNoHits);
            Assert.Equal("Found 1 hit.", viewModel.StatusMessage);
            Assert.True(viewModel.HasRawJson);
            Assert.Equal("{\"count\":1}", viewModel.RawJson);

            var hit = viewModel.Hits[0];
            Assert.Equal("server-name", hit.RuleName);
            Assert.Equal("server_name", hit.TokenName);
            Assert.Equal("configs/app.txt", hit.RelativePath);

            service.HuntAsyncHandler = (_, _, _) => Task.FromException<HuntResult>(new IOException("fail"));

            await viewModel.RunHuntCommand.ExecuteAsync(null);

            Assert.True(viewModel.HasError);
            Assert.Equal("fail", viewModel.ErrorMessage);
            Assert.Equal(0, viewModel.ResultCount);
            Assert.False(viewModel.HasHits);
            Assert.True(viewModel.HasNoHits);
            Assert.False(viewModel.HasRawJson);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void Validation_requires_existing_path()
    {
        var service = new FakeDriftbusterService();
        var viewModel = new HuntViewModel(service);

        Assert.True(viewModel.HasDirectoryError);

        var file = Path.GetTempFileName();
        try
        {
            viewModel.DirectoryPath = file;
            Assert.False(viewModel.HasDirectoryError);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
