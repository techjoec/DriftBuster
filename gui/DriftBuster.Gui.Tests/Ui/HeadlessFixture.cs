using System;
using System.Threading.Tasks;

using Avalonia.Headless;

using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

public sealed class HeadlessFixture : IAsyncLifetime
{
    private IDisposable? _scope;

    public Task InitializeAsync()
    {
        _scope = Program.EnsureHeadless(builder => builder.UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = true,
        }));

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _scope?.Dispose();
        _scope = null;
        return Task.CompletedTask;
    }
}
