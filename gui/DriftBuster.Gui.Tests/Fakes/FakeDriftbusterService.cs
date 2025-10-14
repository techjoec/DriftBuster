using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Tests.Fakes;

internal sealed class FakeDriftbusterService : IDriftbusterService
{
    public Func<CancellationToken, Task<string>>? PingAsyncHandler { get; set; }

    public Func<IEnumerable<string?>, CancellationToken, Task<DiffResult>>? DiffAsyncHandler { get; set; }

    public Func<string?, string?, CancellationToken, Task<HuntResult>>? HuntAsyncHandler { get; set; }

    public Func<CancellationToken, Task<RunProfileListResult>>? ListProfilesHandler { get; set; }

    public Func<RunProfileDefinition, CancellationToken, Task>? SaveProfileHandler { get; set; }

    public Func<RunProfileDefinition, bool, CancellationToken, Task<RunProfileRunResult>>? RunProfileHandler { get; set; }

    public string PingResponse { get; set; } = "pong";

    public DiffResult DiffResponse { get; set; } = new();

    public HuntResult HuntResponse { get; set; } = new();

    public Task<string> PingAsync(CancellationToken cancellationToken = default)
    {
        if (PingAsyncHandler is not null)
        {
            return PingAsyncHandler(cancellationToken);
        }

        return Task.FromResult(PingResponse);
    }

    public Task<DiffResult> DiffAsync(IEnumerable<string?> versions, CancellationToken cancellationToken = default)
    {
        if (DiffAsyncHandler is not null)
        {
            return DiffAsyncHandler(versions, cancellationToken);
        }

        return Task.FromResult(DiffResponse);
    }

    public Task<HuntResult> HuntAsync(string? directory, string? pattern, CancellationToken cancellationToken = default)
    {
        if (HuntAsyncHandler is not null)
        {
            return HuntAsyncHandler(directory, pattern, cancellationToken);
        }

        return Task.FromResult(HuntResponse);
    }

    public Task<RunProfileListResult> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (ListProfilesHandler is not null)
        {
            return ListProfilesHandler(cancellationToken);
        }

        return Task.FromResult(new RunProfileListResult());
    }

    public Task SaveProfileAsync(RunProfileDefinition profile, CancellationToken cancellationToken = default)
    {
        if (SaveProfileHandler is not null)
        {
            return SaveProfileHandler(profile, cancellationToken) ?? Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    public Task<RunProfileRunResult> RunProfileAsync(RunProfileDefinition profile, bool saveProfile, CancellationToken cancellationToken = default)
    {
        if (RunProfileHandler is not null)
        {
            return RunProfileHandler(profile, saveProfile, cancellationToken);
        }

        return Task.FromResult(new RunProfileRunResult());
    }
}
