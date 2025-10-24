using System.IO;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;

using DriftBuster.Backend.Models;
using DriftBuster.Gui;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;

using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class RunProfilesViewInteractionTests
{
    [AvaloniaFact]
    public void BrowseFile_without_storage_provider_keeps_existing_path()
    {
        HeadlessFixture.EnsureFonts();

        var viewModel = CreateViewModel();
        var entry = viewModel.Sources[0];
        entry.Path = "original";

        var view = new RunProfilesView
        {
            DataContext = viewModel,
        };

        Invoke(view, "OnBrowseFile", new Button { Tag = entry }, new RoutedEventArgs(Button.ClickEvent));

        entry.Path.Should().Be("original");
    }

    [AvaloniaFact]
    public void BrowseFile_with_override_updates_path()
    {
        HeadlessFixture.EnsureFonts();

        var viewModel = CreateViewModel();
        var entry = viewModel.Sources[0];

        var view = new RunProfilesView
        {
            DataContext = viewModel,
            FilePickerOverride = () => Task.FromResult<string?>("/tmp/data.json"),
        };

        Invoke(view, "OnBrowseFile", new Button { Tag = entry }, new RoutedEventArgs(Button.ClickEvent));

        entry.Path.Should().Be("/tmp/data.json");
    }

    [AvaloniaFact]
    public void BrowseFolder_without_storage_provider_keeps_existing_path()
    {
        HeadlessFixture.EnsureFonts();

        var viewModel = CreateViewModel();
        var entry = viewModel.Sources[0];
        entry.Path = "directory";

        var view = new RunProfilesView
        {
            DataContext = viewModel,
        };

        Invoke(view, "OnBrowseFolder", new Button { Tag = entry }, new RoutedEventArgs(Button.ClickEvent));

        entry.Path.Should().Be("directory");
    }

    [AvaloniaFact]
    public void BrowseFolder_with_override_updates_path()
    {
        HeadlessFixture.EnsureFonts();

        var viewModel = CreateViewModel();
        var entry = viewModel.Sources[0];

        var view = new RunProfilesView
        {
            DataContext = viewModel,
            FolderPickerOverride = () => Task.FromResult<string?>("/tmp/folder"),
        };

        Invoke(view, "OnBrowseFolder", new Button { Tag = entry }, new RoutedEventArgs(Button.ClickEvent));

        entry.Path.Should().Be("/tmp/folder");
    }

    [AvaloniaFact]
    public void PrepareOfflineCollector_without_storage_provider_skips_backend_call()
    {
        HeadlessFixture.EnsureFonts();

        var service = new FakeDriftbusterService();
        var viewModel = new RunProfilesViewModel(service)
        {
            ProfileName = "example profile",
        };

        var invoked = false;
        service.PrepareOfflineCollectorHandler = (_, _, _) =>
        {
            invoked = true;
            return Task.FromResult(new OfflineCollectorResult());
        };

        var view = new RunProfilesView
        {
            DataContext = viewModel,
        };

        Invoke(view, "OnPrepareOfflineCollector", new Button(), new RoutedEventArgs(Button.ClickEvent));

        invoked.Should().BeFalse();
    }

    [AvaloniaFact]
    public void PrepareOfflineCollector_with_override_invokes_backend()
    {
        HeadlessFixture.EnsureFonts();

        var service = new FakeDriftbusterService();
        var viewModel = new RunProfilesViewModel(service)
        {
            ProfileName = "example profile",
        };

        var tempFile = Path.GetTempFileName();
        viewModel.Sources[0].Path = tempFile;

        var triggered = false;
        service.PrepareOfflineCollectorHandler = (_, request, _) =>
        {
            triggered = true;
            request.PackagePath.Should().Be("/tmp/collector.zip");
            return Task.FromResult(new OfflineCollectorResult());
        };

        var view = new RunProfilesView
        {
            DataContext = viewModel,
            SaveFilePickerOverride = () => Task.FromResult<string?>("/tmp/collector.zip"),
        };

        Invoke(view, "OnPrepareOfflineCollector", new Button(), new RoutedEventArgs(Button.ClickEvent));

        triggered.Should().BeTrue();

        File.Delete(tempFile);
    }

    [AvaloniaFact]
    public void SecretScannerSettings_without_owner_exits_cleanly()
    {
        HeadlessFixture.EnsureFonts();

        var viewModel = CreateViewModel();
        viewModel.SecretScanner = new SecretScannerOptions
        {
            IgnoreRules = new[] { "rule" },
            IgnorePatterns = new[] { "pattern" },
        };

        var view = new RunProfilesView
        {
            DataContext = viewModel,
        };

        Invoke(view, "OnSecretScannerSettings", new Button(), new RoutedEventArgs(Button.ClickEvent));

        viewModel.SecretScannerSummary.Should().Contain("Ignored rules: 1, patterns: 1");
    }

    [AvaloniaFact]
    public void SecretScannerSettings_with_override_applies_changes()
    {
        HeadlessFixture.EnsureFonts();

        var viewModel = CreateViewModel();
        var view = new RunProfilesView
        {
            DataContext = viewModel,
            SecretScannerDialogOverride = vm =>
            {
                vm.IgnoreRules[0].Value = "rule-x";
                vm.IgnorePatterns[0].Value = "pattern-y";
                return Task.FromResult<SecretScannerOptions?>(vm.BuildResult());
            },
        };

        Invoke(view, "OnSecretScannerSettings", new Button(), new RoutedEventArgs(Button.ClickEvent));

        viewModel.SecretScanner.IgnoreRules.Should().Contain("rule-x");
        viewModel.SecretScanner.IgnorePatterns.Should().Contain("pattern-y");
    }

    [AvaloniaFact]
    public void BuildOfflineCollectorName_sanitizes_profile_name()
    {
        var method = typeof(RunProfilesView).GetMethod("BuildOfflineCollectorName", BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var result = method!.Invoke(null, new object?[] { "  Report*Collector  " }) as string;

        result.Should().NotBeNull();
        result!.Should().EndWith("-offline-collector.zip");
        result.Should().NotContain(" ");
        result.Should().NotBe("offline-collector.zip");
    }

    private static RunProfilesViewModel CreateViewModel()
    {
        return new RunProfilesViewModel(new FakeDriftbusterService());
    }

    private static void Invoke(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(target, args);
    }
}
