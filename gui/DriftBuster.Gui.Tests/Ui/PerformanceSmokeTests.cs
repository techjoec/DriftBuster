using System;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Gui.Services;

using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

public sealed class PerformanceSmokeTests
{
    [Fact]
    [Trait("Category", "PerfSmoke")]
    public void PerformanceProfile_respects_environment_overrides()
    {
        var originalThreshold = Environment.GetEnvironmentVariable("DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD");
        var originalForce = Environment.GetEnvironmentVariable("DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION");

        try
        {
            Environment.SetEnvironmentVariable("DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD", "512");
            Environment.SetEnvironmentVariable("DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION", "false");

            var profile = PerformanceProfile.FromEnvironment();
            Assert.Equal(512, profile.VirtualizationThreshold);
            Assert.False(profile.ShouldVirtualize(600));

            Environment.SetEnvironmentVariable("DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION", "true");
            profile = PerformanceProfile.FromEnvironment();
            Assert.True(profile.ShouldVirtualize(1));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD", originalThreshold);
            Environment.SetEnvironmentVariable("DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION", originalForce);
        }
    }

    [Fact]
    [Trait("Category", "PerfSmoke")]
    public void ToastService_batches_dispatcher_work_during_burst()
    {
        const int toastCount = 200;
        const int expectedOverflow = toastCount - 3;
        const int maxDispatchCycles = toastCount / 4;

        var reset = new ManualResetEventSlim(false);
        var dispatchCount = 0;

        ToastService? service = null;
        var dispatcher = new Action<Action>(action =>
        {
            dispatchCount++;
            Task.Run(() =>
            {
                action();
                if (service is not null && service.OverflowToasts.Count == expectedOverflow)
                {
                    reset.Set();
                }
            });
        });

        service = new ToastService(dispatcher);

        for (var i = 0; i < toastCount; i++)
        {
            service.Show($"Toast {i}", "Synthetic perf harness toast", ToastLevel.Info, TimeSpan.FromMinutes(10));
        }

        Assert.True(reset.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for toast burst processing.");
        Assert.InRange(dispatchCount, 1, maxDispatchCycles);
        Assert.Equal(3, service.ActiveToasts.Count);
        Assert.Equal(expectedOverflow, service.OverflowToasts.Count);

        var emptyReset = new ManualResetEventSlim(false);
        service.DismissAll();

        var dispatchCountBeforeDismiss = dispatchCount;

        Task.Run(async () =>
        {
            while (service.ActiveToasts.Count > 0 || service.OverflowToasts.Count > 0)
            {
                await Task.Delay(10);
            }

            emptyReset.Set();
        });

        Assert.True(emptyReset.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for toast dismissal to drain.");
        Assert.True(dispatchCount <= dispatchCountBeforeDismiss + 2, $"Expected at most two additional dispatcher cycles but observed {dispatchCount - dispatchCountBeforeDismiss}.");
        Assert.Empty(service.ActiveToasts);
        Assert.Empty(service.OverflowToasts);
    }
}
