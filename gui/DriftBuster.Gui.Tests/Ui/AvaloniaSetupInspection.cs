using System;
using System.Linq;
using System.Reflection;
using Avalonia.Headless.XUnit;

using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

using Xunit;
using Xunit.Abstractions;

namespace DriftBuster.Gui.Tests.Ui;

public sealed class AvaloniaSetupInspection
{
    private readonly ITestOutputHelper _output;

    public AvaloniaSetupInspection(ITestOutputHelper output)
    {
        _output = output;
    }

    [AvaloniaFact]
    public void LogSetupState()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AVALONIA_INSPECT"), "1", StringComparison.Ordinal))
        {
            _output.WriteLine("Skipping diagnostic inspection (set AVALONIA_INSPECT=1 to enable).");
            return;
        }

        var (registeredBefore, runtimeType) = GetRuntimeServicesState();
        _output.WriteLine($"Runtime services type: {runtimeType?.AssemblyQualifiedName ?? "<not found>"}");
        _output.WriteLine($"StandardRuntimePlatformServices.Registered (before): {registeredBefore?.ToString() ?? "<unknown>"}");

        var appBuilder = Program.BuildAvaloniaApp()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });

        _output.WriteLine("Calling SetupWithoutStarting()...");
        appBuilder.SetupWithoutStarting();

        var (registeredAfter, _) = GetRuntimeServicesState();
        _output.WriteLine($"StandardRuntimePlatformServices.Registered (after): {registeredAfter?.ToString() ?? "<unknown>"}");

        var application = Application.Current;
        if (application is null)
        {
            _output.WriteLine("Application.Current is null after setup.");
            return;
        }

        _output.WriteLine($"Application type: {application.GetType().FullName}");
        _output.WriteLine($"Styles count: {application.Styles.Count}");

        for (var index = 0; index < application.Styles.Count; index++)
        {
            var style = application.Styles[index];
            switch (style)
            {
                case StyleInclude include:
                    _output.WriteLine($"[{index}] StyleInclude: {include.Source}");
                    break;
                case Styles nested:
                    _output.WriteLine($"[{index}] Styles group with {nested.Count} entries");
                    for (var nestedIndex = 0; nestedIndex < nested.Count; nestedIndex++)
                    {
                        var nestedStyle = nested[nestedIndex];
                        switch (nestedStyle)
                        {
                            case StyleInclude nestedInclude:
                                _output.WriteLine($"  [{index}.{nestedIndex}] StyleInclude: {nestedInclude.Source}");
                                break;
                            default:
                                _output.WriteLine($"  [{index}.{nestedIndex}] {nestedStyle.GetType().FullName}");
                                break;
                        }
                    }
                    break;
                default:
                    _output.WriteLine($"[{index}] {style.GetType().FullName}");
                    break;
            }
        }

        var resources = application.Resources.MergedDictionaries;
        _output.WriteLine($"Merged dictionaries: {resources.Count}");
        for (var index = 0; index < resources.Count; index++)
        {
            var dictionary = resources[index];
            if (dictionary is ResourceInclude include)
            {
                _output.WriteLine($"[Merged:{index}] ResourceInclude: {include.Source}");
            }
            else
            {
                _output.WriteLine($"[Merged:{index}] {dictionary?.GetType().FullName ?? "null"}");
            }
        }
    }

    private static (bool? registered, Type? type) GetRuntimeServicesState()
    {
        var type = Type.GetType("Avalonia.Platform.StandardRuntimePlatformServices, Avalonia") ??
                   Type.GetType("Avalonia.Platform.StandardRuntimePlatformServices, Avalonia.Base");

        if (type is null)
        {
            return (null, null);
        }

        var field = type.GetField("registered", BindingFlags.NonPublic | BindingFlags.Static);
        if (field is null)
        {
            return (null, type);
        }

        var value = field.GetValue(null);
        if (value is bool boolValue)
        {
            return (boolValue, type);
        }

        return (null, type);
    }
}