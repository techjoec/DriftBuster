using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

using DriftBuster.Backend.Registry;

namespace DriftBuster.Gui.Views;

/// <summary>Asks for a user name and password and saves them as a PSCredential file; closes with the file's path, or null.</summary>
[ExcludeFromCodeCoverage]
public partial class CredentialWindow : Window
{
    private readonly string _path = string.Empty;

    public CredentialWindow()
    {
        InitializeComponent();
    }

    public CredentialWindow(string computer, string path)
        : this()
    {
        _path = path;
        this.FindControl<TextBlock>("Heading")!.Text = $"Sign-in for {computer}";
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        var user = this.FindControl<TextBox>("UserName")!.Text?.Trim() ?? string.Empty;
        var password = this.FindControl<TextBox>("Password")!.Text ?? string.Empty;
        var error = this.FindControl<TextBlock>("Error")!;
        if (user.Length == 0 || password.Length == 0)
        {
            error.Text = "Enter a user name and a password.";
            error.IsVisible = true;
            return;
        }

        var save = this.FindControl<Button>("SaveButton")!;
        save.IsEnabled = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await Task.Run(() => RemoteRegistryTreeReader.SaveCredential(_path, user, password)).ConfigureAwait(true);
            Close(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            error.Text = ex.Message;
            error.IsVisible = true;
            save.IsEnabled = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
