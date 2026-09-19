using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views;

/// <summary>
/// Shows a bug report in full and sends it only after the user confirms they checked it: as a prefilled GitHub issue in the
/// browser, or to a configured receiver.
/// </summary>
[ExcludeFromCodeCoverage]
public partial class BugReportWindow : Window
{
    private readonly BugReportSender _sender = new();

    public BugReportWindow()
    {
        InitializeComponent();
    }

    public BugReportWindow(BugReportDraft draft, bool preferApi)
        : this()
    {
        DataContext = draft;
        var send = this.FindControl<Button>("SendApi")!;
        ToolTip.SetTip(send, _sender.IsConfigured
            ? $"Posts the payload to {_sender.Endpoint}"
            : $"No receiver is configured. Set {BugReportSender.EndpointVariable} to the receiver's address.");
        if (preferApi)
        {
            Status($"Sending goes to {(_sender.IsConfigured ? _sender.Endpoint : "a receiver, once one is configured")}.");
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private BugReportDraft Draft => (BugReportDraft)DataContext!;

    private void Status(string text) => this.FindControl<TextBlock>("StatusText")!.Text = text;

    private void OnCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var confirmed = this.FindControl<CheckBox>("Checked")!.IsChecked == true;
        this.FindControl<Button>("OpenIssue")!.IsEnabled = confirmed;
        this.FindControl<Button>("SendApi")!.IsEnabled = confirmed && _sender.IsConfigured;
    }

    private void OnOpenIssue(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Draft.GitHubIssueUrl()) { UseShellExecute = true });
            Status("Opened the issue form in your browser; submit it there.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Status($"Could not open the browser: {ex.Message}");
        }
    }

    private async void OnSendApi(object? sender, RoutedEventArgs e)
    {
        Status("Sending…");
        Status(await _sender.SendAsync(Draft.PayloadText).ConfigureAwait(true));
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
