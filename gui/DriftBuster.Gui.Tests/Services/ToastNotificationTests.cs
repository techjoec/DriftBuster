using System;
using System.Threading.Tasks;

using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Tests.Services;

public sealed class ToastNotificationTests
{
    [Fact]
    public void Dismiss_command_invokes_dismiss_callback()
    {
        Guid? dismissed = null;
        var id = Guid.NewGuid();
        var notification = new ToastNotification(
            id,
            "Info",
            "Body",
            ToastLevel.Info,
            TimeSpan.FromSeconds(2),
            primaryAction: null,
            secondaryAction: null,
            dismiss: value => dismissed = value);

        notification.DismissCommand.Execute(null);

        dismissed.Should().Be(id);
        notification.PrimaryCommand.Should().BeNull();
        notification.SecondaryCommand.Should().BeNull();
    }

    [Fact]
    public async Task Primary_and_secondary_actions_run_callbacks()
    {
        var id = Guid.NewGuid();
        var primaryRuns = 0;
        var secondaryRuns = 0;
        Guid? dismissed = null;

        var notification = new ToastNotification(
            id,
            "Actions",
            "Body",
            ToastLevel.Warning,
            TimeSpan.FromSeconds(5),
            new ToastAction("Primary", () =>
            {
                primaryRuns++;
                return Task.CompletedTask;
            }, CloseOnInvoke: true),
            new ToastAction("Secondary", () =>
            {
                secondaryRuns++;
                return Task.CompletedTask;
            }, CloseOnInvoke: false),
            dismiss: value => dismissed = value);

        notification.PrimaryLabel.Should().Be("Primary");
        notification.SecondaryLabel.Should().Be("Secondary");

        await notification.PrimaryCommand!.ExecuteAsync(null);
        await notification.SecondaryCommand!.ExecuteAsync(null);

        primaryRuns.Should().Be(1);
        secondaryRuns.Should().Be(1);
        dismissed.Should().Be(id);
    }

    [Fact]
    public void Exposes_timestamp_and_level_labels()
    {
        var notification = new ToastNotification(
            Guid.NewGuid(),
            "Title",
            "Message",
            ToastLevel.Error,
            TimeSpan.FromSeconds(1),
            primaryAction: null,
            secondaryAction: null,
            dismiss: _ => { });

        notification.LevelLabel.Should().Be("Error");
        notification.TimestampText.Should().NotBeNullOrWhiteSpace();
    }
}
