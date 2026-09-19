using System.Diagnostics.CodeAnalysis;

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace DriftBuster.Gui.Views;

/// <summary>A read-only text with a heading and an optional note: raw file text, placeholders, anything to read and copy.</summary>
[ExcludeFromCodeCoverage]
public partial class TextViewerWindow : Window
{
    public TextViewerWindow()
    {
        InitializeComponent();
    }

    /// <param name="wrap">Wraps the text and sizes the window to it, for a message rather than a file.</param>
    public TextViewerWindow(string title, string heading, string text, string? note = null, bool wrap = false)
        : this()
    {
        Title = title;
        this.FindControl<TextBlock>("Heading")!.Text = heading;
        var body = this.FindControl<TextBox>("Body")!;
        body.Text = text;
        if (wrap)
        {
            body.TextWrapping = TextWrapping.Wrap;
            SizeToContent = SizeToContent.Height;
        }

        if (string.IsNullOrEmpty(text))
        {
            // Nothing to show below the heading and note: drop the empty box and size to what is left.
            body.IsVisible = false;
            SizeToContent = SizeToContent.Height;
        }
        if (!string.IsNullOrWhiteSpace(note))
        {
            this.FindControl<TextBlock>("Note")!.Text = note;
            this.FindControl<Border>("NoteBorder")!.IsVisible = true;
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(this.FindControl<TextBox>("Body")!.Text ?? string.Empty).ConfigureAwait(true);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
