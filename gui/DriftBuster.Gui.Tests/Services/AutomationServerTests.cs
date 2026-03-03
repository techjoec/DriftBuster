#if DEBUG
#pragma warning disable MA0004

using System.Text.Json;

using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Tests.Services;

public sealed class AutomationServerTests
{
    [Fact]
    public async Task DispatchWithTimeoutAsync_returns_dispatch_result_when_completed_in_time()
    {
        using var cts = new CancellationTokenSource();

        var response = await AutomationServer.DispatchWithTimeoutAsync(
            () => Task.FromResult("ok-response"),
            TimeSpan.FromMilliseconds(100),
            cts.Token);

        response.Should().Be("ok-response");
    }

    [Fact]
    public async Task DispatchWithTimeoutAsync_returns_timeout_error_when_dispatch_exceeds_timeout()
    {
        using var cts = new CancellationTokenSource();

        var response = await AutomationServer.DispatchWithTimeoutAsync(
            async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(120));
                return "late-response";
            },
            TimeSpan.FromMilliseconds(10),
            cts.Token);

        using var doc = JsonDocument.Parse(response);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString().Should().Contain("timed out");
    }

    [Fact]
    public async Task DispatchWithTimeoutAsync_honors_cancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(15));

        Func<Task> act = async () => await AutomationServer.DispatchWithTimeoutAsync(
            async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                return "late-response";
            },
            TimeSpan.FromSeconds(5),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("250", 250)]
    [InlineData("0007", 7)]
    public void ParseUiDispatchTimeout_parses_positive_integer_milliseconds(string candidate, int expectedMs)
    {
        var timeout = AutomationServer.ParseUiDispatchTimeout(candidate);

        timeout.Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void ParseUiDispatchTimeout_falls_back_to_default_for_invalid_values(string? candidate)
    {
        var timeout = AutomationServer.ParseUiDispatchTimeout(candidate);

        timeout.Should().Be(AutomationServer.DefaultUiDispatchTimeout);
    }
}
#endif
