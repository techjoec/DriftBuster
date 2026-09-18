using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views
{
    [ExcludeFromCodeCoverage]
    public partial class DiffView : UserControl
    {
        internal Func<Task<string?>>? FilePickerOverride { get; set; }
        internal Func<string, Task>? ClipboardSetTextOverride { get; set; }
        internal IStorageProvider? StorageProviderOverride { get; set; }

        private CompareViewModel? _settings;

        public DiffView()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => WatchSettings((DataContext as DiffViewModel)?.Settings);
            AttachedToVisualTree += (_, _) => WatchSettings((DataContext as DiffViewModel)?.Settings);
            DetachedFromVisualTree += (_, _) => WatchSettings(null);
        }

        private void WatchSettings(CompareViewModel? settings)
        {
            if (_settings is not null)
            {
                _settings.PropertyChanged -= OnSettingsChanged;
            }

            _settings = settings;
            if (_settings is not null)
            {
                _settings.PropertyChanged += OnSettingsChanged;
            }
        }

        // A fresh comparison lands below the inputs; scroll it into view so the answer is on screen.
        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(CompareViewModel.HasData), StringComparison.Ordinal) || _settings is not { HasData: true })
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (this.FindControl<StackPanel>("SettingsSection") is { } section)
                {
                    section.BringIntoView(new Rect(0, 0, section.Bounds.Width, Math.Min(section.Bounds.Height, 480)));
                }
            }, DispatcherPriority.Background);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private async void OnBrowseFile(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not DiffViewModel vm)
            {
                return;
            }

            if (sender is not Button button || button.Tag is not DiffViewModel.DiffInput input)
            {
                return;
            }

            var file = await PickSingleFileAsync().ConfigureAwait(true);
            if (file is not null)
            {
                input.Path = file;
            }
        }

        private async Task<string?> PickSingleFileAsync()
        {
            if (FilePickerOverride is not null)
            {
                return await FilePickerOverride().ConfigureAwait(true);
            }

            var storageProvider = StorageProviderOverride ?? TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storageProvider is null)
            {
                return null;
            }

            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
            }).ConfigureAwait(true);

            if (files.Count == 0)
            {
                return null;
            }

            return files[0].TryGetLocalPath();
        }

        private async void OnCopyActiveJson(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not DiffViewModel vm)
            {
                return;
            }

            if (!vm.TryGetCopyPayload(out var payload))
            {
                return;
            }

            if (ClipboardSetTextOverride is not null)
            {
                await ClipboardSetTextOverride(payload).ConfigureAwait(true);
                return;
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            await clipboard.SetTextAsync(payload).ConfigureAwait(true);
        }

        private async void OnCopyDiff(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            if (button.Tag is not string diffText || string.IsNullOrWhiteSpace(diffText))
            {
                return;
            }

            if (ClipboardSetTextOverride is not null)
            {
                await ClipboardSetTextOverride(diffText).ConfigureAwait(true);
                return;
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            await clipboard.SetTextAsync(diffText).ConfigureAwait(true);
        }
    }
}
