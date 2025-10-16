using System;
using System.IO;
using System.Threading.Tasks;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

using Xunit;

namespace DriftBuster.Gui.Tests.ViewModels;

public class HuntViewModelAdditionalTests
{
    [Fact]
    public async Task HitView_trims_long_excerpt_and_handles_missing_token()
    {
        var longExcerpt = new string('x', 200);
        var service = new FakeDriftbusterService
        {
            HuntResponse = new HuntResult
            {
                Directory = "dir",
                Pattern = "p",
                Count = 1,
                Hits = new[]
                {
                    new HuntHit
                    {
                        Rule = new HuntRuleSummary { Name = "r", Description = "d", TokenName = "" },
                        RelativePath = "a",
                        Path = "a",
                        LineNumber = 1,
                        Excerpt = longExcerpt,
                    },
                },
                RawJson = "{}",
            },
        };

        var temp = Path.GetTempFileName();
        try
        {
            var vm = new HuntViewModel(service)
            {
                DirectoryPath = temp,
                Pattern = "p",
            };

            await vm.RunHuntCommand.ExecuteAsync(null);

            var hit = Assert.Single(vm.Hits);
            Assert.False(hit.HasToken);
            Assert.Equal("—", hit.TokenName);
            Assert.EndsWith("…", hit.Excerpt);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Initial_status_message_surfaces_via_constructor()
    {
        var vm = new HuntViewModel(new FakeDriftbusterService(), initial: "hello");
        Assert.True(vm.HasStatus);
        Assert.Equal("hello", vm.StatusMessage);
    }
}

