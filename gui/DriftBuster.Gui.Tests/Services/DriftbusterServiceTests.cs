using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Tests.Services;

public sealed class DriftbusterServiceTests
{
    [Fact]
    public async Task Methods_delegate_to_backend()
    {
        var backend = new RecordingBackend();
        var service = new DriftbusterService(backend);

        await service.PingAsync();
        await service.DiffAsync(new[] { "a", "b" });
        await service.HuntAsync("dir", "pattern");
        await service.ListProfilesAsync();
        await service.SaveProfileAsync(new RunProfileDefinition { Name = "profile" });
        await service.RunProfileAsync(new RunProfileDefinition { Name = "profile" }, saveProfile: true);
        await service.PrepareOfflineCollectorAsync(new RunProfileDefinition { Name = "profile" }, new OfflineCollectorRequest());

        backend.PingCalled.Should().BeTrue();
        backend.DiffVersions.Should().Equal("a", "b");
        backend.HuntDirectory.Should().Be("dir");
        backend.HuntPattern.Should().Be("pattern");
        backend.SavedProfileNames.Should().ContainSingle().Which.Should().Be("profile");
        backend.RunInvocations.Should().Be(1);
        backend.OfflineCollectorCalls.Should().Be(1);
    }

    private sealed class RecordingBackend : IDriftbusterBackend
    {
        public bool PingCalled { get; private set; }
        public IReadOnlyList<string?> DiffVersions { get; private set; } = new List<string?>();
        public string? HuntDirectory { get; private set; }
        public string? HuntPattern { get; private set; }
        public List<string> SavedProfileNames { get; } = new();
        public int RunInvocations { get; private set; }
        public int OfflineCollectorCalls { get; private set; }

        public Task<string> PingAsync(CancellationToken cancellationToken = default)
        {
            PingCalled = true;
            return Task.FromResult("pong");
        }

        public Task<DiffResult> DiffAsync(IEnumerable<string?> versions, CancellationToken cancellationToken = default)
        {
            DiffVersions = versions.ToList();
            return Task.FromResult(new DiffResult());
        }

        public Task<HuntResult> HuntAsync(string? directory, string? pattern, CancellationToken cancellationToken = default)
        {
            HuntDirectory = directory;
            HuntPattern = pattern;
            return Task.FromResult(new HuntResult());
        }

        public Task<RunProfileListResult> ListProfilesAsync(string? baseDir = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RunProfileListResult());
        }

        public Task SaveProfileAsync(RunProfileDefinition profile, string? baseDir = null, CancellationToken cancellationToken = default)
        {
            SavedProfileNames.Add(profile.Name ?? string.Empty);
            return Task.CompletedTask;
        }

        public Task<RunProfileRunResult> RunProfileAsync(RunProfileDefinition profile, bool saveProfile, string? baseDir = null, string? timestamp = null, CancellationToken cancellationToken = default)
        {
            RunInvocations++;
            return Task.FromResult(new RunProfileRunResult());
        }

        public Task<OfflineCollectorResult> PrepareOfflineCollectorAsync(RunProfileDefinition profile, OfflineCollectorRequest request, string? baseDir = null, CancellationToken cancellationToken = default)
        {
            OfflineCollectorCalls++;
            return Task.FromResult(new OfflineCollectorResult());
        }
    }
}
