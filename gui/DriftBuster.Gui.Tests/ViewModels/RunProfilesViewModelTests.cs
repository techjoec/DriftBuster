using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

using Xunit;

namespace DriftBuster.Gui.Tests.ViewModels;

public class RunProfilesViewModelTests
{
    [Fact]
    public void Validation_populates_errors_and_disables_commands_until_valid()
    {
        var service = new FakeDriftbusterService();
        var viewModel = new RunProfilesViewModel(service);

        Assert.Equal("Select a baseline path.", viewModel.Sources[0].Error);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.RunCommand.CanExecute(null));

        viewModel.ProfileName = "sample";

        var baseline = Path.GetTempFileName();

        try
        {
            viewModel.Sources[0].Path = baseline;

            Assert.Null(viewModel.Sources[0].Error);
            Assert.True(viewModel.SaveCommand.CanExecute(null));
            Assert.True(viewModel.RunCommand.CanExecute(null));

            viewModel.AddSourceCommand.Execute(null);

            var second = viewModel.Sources[1];
            Assert.Equal("Select a source path.", second.Error);

            var invalid = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            second.Path = invalid;

            Assert.Equal("Path does not exist.", second.Error);
            Assert.False(viewModel.SaveCommand.CanExecute(null));
            Assert.False(viewModel.RunCommand.CanExecute(null));

            second.Path = string.Empty;
            Assert.Equal("Select a source path.", second.Error);
            Assert.True(viewModel.SaveCommand.CanExecute(null));
            Assert.True(viewModel.RunCommand.CanExecute(null));
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public async Task Run_populates_results_and_enables_open_output_command()
    {
        var output = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var baseline = Path.GetTempFileName();

        try
        {
            var service = new FakeDriftbusterService
            {
                RunProfileHandler = (_, _, _) => Task.FromResult(new RunProfileRunResult
                {
                    OutputDir = output.FullName,
                    Files = new[]
                    {
                        new RunProfileFileResult
                        {
                            Source = "B",
                            Destination = Path.Combine(output.FullName, "b.txt").Replace(Path.DirectorySeparatorChar, '/'),
                            Size = 1,
                            Sha256 = "hash-b",
                        },
                        new RunProfileFileResult
                        {
                            Source = "A",
                            Destination = Path.Combine(output.FullName, "a.txt").Replace(Path.DirectorySeparatorChar, '/'),
                            Size = 2048,
                            Sha256 = "hash-a",
                        },
                    },
                }),
            };

            var viewModel = new RunProfilesViewModel(service)
            {
                ProfileName = "run-test",
            };

            viewModel.Sources[0].Path = baseline;

            await viewModel.RunCommand.ExecuteAsync(null);

            Assert.True(viewModel.HasRunResults);
            Assert.Equal("Run complete. Files copied: 2.", viewModel.StatusMessage);
            var results = viewModel.RunResults.ToList();
            Assert.Equal(2, results.Count);
            Assert.Equal(new[] { "A", "B" }, results.Select(r => r.Source));
            Assert.Equal("2,048 bytes", results[0].Size);
            Assert.Equal("1 byte", results[1].Size);
            Assert.Equal("hash-a", results[0].Hash);
            Assert.True(viewModel.OpenOutputCommand.CanExecute(null));
        }
        finally
        {
            File.Delete(baseline);
            Directory.Delete(output.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareOfflineCollector_saves_profile_and_invokes_backend()
    {
        var baseline = Path.GetTempFileName();
        var packagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");

        try
        {
            var service = new FakeDriftbusterService();
            RunProfileDefinition? savedProfile = null;
            OfflineCollectorRequest? capturedRequest = null;

            service.SaveProfileHandler = (profile, _) =>
            {
                savedProfile = profile;
                return Task.CompletedTask;
            };

            service.PrepareOfflineCollectorHandler = (profile, request, _) =>
            {
                capturedRequest = request;
                Assert.Equal("collector", profile.Name);
                Assert.Single(profile.SecretScanner.IgnoreRules);
                Assert.Equal("rule-ignore", profile.SecretScanner.IgnoreRules[0]);
                return Task.FromResult(new OfflineCollectorResult
                {
                    PackagePath = request.PackagePath,
                    ConfigFileName = "collector.offline.config.json",
                    ScriptFileName = "driftbuster-offline-runner.ps1",
                });
            };

            var viewModel = new RunProfilesViewModel(service)
            {
                ProfileName = "collector",
            };

            viewModel.Sources[0].Path = baseline;
            viewModel.ApplySecretScanner(new SecretScannerOptions
            {
                IgnoreRules = new[] { "rule-ignore" },
                IgnorePatterns = new[] { "ALLOW_ME" },
            });

            await viewModel.PrepareOfflineCollectorAsync(packagePath);

            Assert.NotNull(savedProfile);
            Assert.NotNull(capturedRequest);
            Assert.Equal(packagePath, capturedRequest?.PackagePath);
            Assert.Contains("collector", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(viewModel.IsBusy);
        }
        finally
        {
            File.Delete(baseline);
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }
        }
    }

    [Fact]
    public async Task Save_persists_schedule_manifest()
    {
        var baseline = Path.GetTempFileName();

        try
        {
            var service = new FakeDriftbusterService
            {
                ListSchedulesHandler = _ => Task.FromResult(new ScheduleListResult()),
            };

            var captured = new List<ScheduleDefinition>();
            service.SaveSchedulesHandler = (schedules, _) =>
            {
                captured = schedules.ToList();
                return Task.CompletedTask;
            };

            service.SaveProfileHandler = (profile, _) => Task.CompletedTask;

            var viewModel = new RunProfilesViewModel(service)
            {
                ProfileName = "nightly",
            };

            viewModel.Sources[0].Path = baseline;
            viewModel.AddScheduleCommand.Execute(null);
            var schedule = Assert.Single(viewModel.Schedules);
            schedule.Name = "nightly-run";
            schedule.Every = "24h";
            schedule.TagsText = "env:prod, nightly";
            schedule.Metadata.Add(new RunProfilesViewModel.KeyValueEntry { Key = "contact", Value = "oncall@example.com" });

            await viewModel.SaveCommand.ExecuteAsync(null);

            var saved = Assert.Single(captured);
            Assert.Equal("nightly-run", saved.Name);
            Assert.Equal("nightly", saved.Profile);
            Assert.Equal("24h", saved.Every);
            Assert.Equal(new[] { "env:prod", "nightly" }, saved.Tags);
            Assert.Equal("oncall@example.com", saved.Metadata["contact"]);
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public void ProfileSuggestions_include_existing_profiles_and_current_name()
    {
        var service = new FakeDriftbusterService();
        var viewModel = new RunProfilesViewModel(service)
        {
            ProfileName = "Nightly",
        };

        viewModel.Profiles.Add(new RunProfileDefinition { Name = "Weekly" });
        viewModel.Profiles.Add(new RunProfileDefinition { Name = "daily" });
        viewModel.Profiles.Add(new RunProfileDefinition { Name = "Nightly" });

        viewModel.ProfileSuggestions.Should().ContainInOrder("Nightly", "daily", "Weekly");
        viewModel.ProfileSuggestions.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ProfileName_updates_blank_schedule_profiles_but_preserves_custom_names()
    {
        var service = new FakeDriftbusterService();
        var viewModel = new RunProfilesViewModel(service)
        {
            ProfileName = "Alpha",
        };

        viewModel.AddScheduleCommand.Execute(null);
        var schedule = Assert.Single(viewModel.Schedules);
        schedule.Profile.Should().Be("Alpha");

        viewModel.ProfileName = "Beta";
        schedule.Profile.Should().Be("Beta");

        schedule.Profile = "Custom";
        viewModel.ProfileName = "Gamma";
        schedule.Profile.Should().Be("Custom");
    }

    [Fact]
    public void Schedule_validation_requires_required_fields()
    {
        var baseline = Path.GetTempFileName();

        try
        {
            var service = new FakeDriftbusterService();
            var viewModel = new RunProfilesViewModel(service)
            {
                ProfileName = "nightly",
            };

            viewModel.Sources[0].Path = baseline;
            viewModel.AddScheduleCommand.Execute(null);

            Assert.False(viewModel.SaveCommand.CanExecute(null));

            var schedule = Assert.Single(viewModel.Schedules);
            schedule.Name = "nightly";
            schedule.Profile = "nightly";

            Assert.False(viewModel.SaveCommand.CanExecute(null));

            schedule.Every = "24h";

            Assert.True(viewModel.SaveCommand.CanExecute(null));
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public void Blank_schedule_cards_remain_optional_until_edited()
    {
        var baseline = Path.GetTempFileName();

        try
        {
            var viewModel = new RunProfilesViewModel(new FakeDriftbusterService());

            viewModel.Sources[0].Path = baseline;
            viewModel.AddScheduleCommand.Execute(null);

            var schedule = Assert.Single(viewModel.Schedules);
            schedule.IsBlank.Should().BeTrue();
            schedule.Error.Should().BeNull();
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public void Partially_filled_schedule_cards_surface_required_errors()
    {
        var baseline = Path.GetTempFileName();

        try
        {
            var viewModel = new RunProfilesViewModel(new FakeDriftbusterService())
            {
                ProfileName = "nightly",
            };

            viewModel.Sources[0].Path = baseline;
            viewModel.AddScheduleCommand.Execute(null);

            var schedule = Assert.Single(viewModel.Schedules);
            schedule.Profile = string.Empty;

            schedule.Name = "nightly";
            schedule.Error.Should().Be("Schedule profile is required.");

            schedule.Profile = "nightly";
            schedule.Error.Should().Be("Schedule interval is required.");

            schedule.Every = "24h";
            schedule.Error.Should().BeNull();
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public void Schedule_window_fields_require_pairs_and_timezone()
    {
        var baseline = Path.GetTempFileName();

        try
        {
            var viewModel = new RunProfilesViewModel(new FakeDriftbusterService())
            {
                ProfileName = "nightly",
            };

            viewModel.Sources[0].Path = baseline;
            viewModel.AddScheduleCommand.Execute(null);

            var schedule = Assert.Single(viewModel.Schedules);
            schedule.Name = "nightly";
            schedule.Profile = "nightly";
            schedule.Every = "24h";

            schedule.WindowStart = "08:00";
            schedule.Error.Should().Be("Specify both window start and end times.");

            schedule.WindowEnd = "17:00";
            schedule.Error.Should().Be("Specify a timezone when defining a window.");

            schedule.WindowTimezone = "UTC";
            schedule.Error.Should().BeNull();
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public void Schedule_definition_trims_window_values()
    {
        var viewModel = new RunProfilesViewModel(new FakeDriftbusterService());
        viewModel.AddScheduleCommand.Execute(null);

        var schedule = Assert.Single(viewModel.Schedules);
        schedule.Name = " nightly ";
        schedule.Profile = " daily ";
        schedule.Every = " 24h ";
        schedule.WindowStart = " 08:00 ";
        schedule.WindowEnd = " 17:00 ";
        schedule.WindowTimezone = " UTC ";

        var definition = schedule.ToDefinition();
        definition.Window.Should().NotBeNull();
        definition.Window!.Start.Should().Be("08:00");
        definition.Window.End.Should().Be("17:00");
        definition.Window.Timezone.Should().Be("UTC");
    }

    [Fact]
    public void Metadata_add_command_revalidates_schedule_entries()
    {
        var viewModel = new RunProfilesViewModel(new FakeDriftbusterService());
        viewModel.AddScheduleCommand.Execute(null);

        var schedule = Assert.Single(viewModel.Schedules);
        schedule.Error.Should().BeNull();

        schedule.AddMetadataCommand.Execute(null);
        var metadata = schedule.Metadata.Should().ContainSingle().Subject;

        metadata.Key = "Environment";
        schedule.Error.Should().Be("Schedule name is required.");
    }

    [Fact]
    public void Removing_metadata_entries_clears_errors_and_detaches_listeners()
    {
        var viewModel = new RunProfilesViewModel(new FakeDriftbusterService());
        viewModel.AddScheduleCommand.Execute(null);

        var schedule = Assert.Single(viewModel.Schedules);
        schedule.AddMetadataCommand.Execute(null);
        var entry = schedule.Metadata.Should().ContainSingle().Subject;

        metadata_Key_causes_error();

        schedule.RemoveMetadataCommand.Execute(entry);
        schedule.Error.Should().BeNull();

        var propertyChangedField = typeof(ObservableObject)
            .GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var invocationList = (propertyChangedField?.GetValue(entry) as MulticastDelegate)?.GetInvocationList() ?? Array.Empty<Delegate>();
        invocationList.Should().NotContain(handler => handler.Method.Name == "OnMetadataEntryPropertyChanged");

        void metadata_Key_causes_error()
        {
            entry.Key = "Environment";
            schedule.Error.Should().Be("Schedule name is required.");
        }
    }

    [Fact]
    public void Tags_are_trimmed_and_deduplicated_in_definitions()
    {
        var viewModel = new RunProfilesViewModel(new FakeDriftbusterService());
        viewModel.AddScheduleCommand.Execute(null);

        var schedule = Assert.Single(viewModel.Schedules);
        schedule.Name = "nightly";
        schedule.Profile = "nightly";
        schedule.Every = "24h";
        schedule.TagsText = "prod; Prod , staging\n staging ";

        var definition = schedule.ToDefinition();
        definition.Tags.Should().BeEquivalentTo(new[] { "prod", "staging" });
    }

    [Fact]
    public async Task RefreshCommand_populates_profiles_and_preserves_selection()
    {
        var service = new FakeDriftbusterService
        {
            ListProfilesHandler = _ => Task.FromResult(new RunProfileListResult
            {
                Profiles = new[]
                {
                    new RunProfileDefinition { Name = "Alpha" },
                    new RunProfileDefinition { Name = "Beta" },
                },
            }),
        };

        var viewModel = new RunProfilesViewModel(service);
        viewModel.ProfileName = "baseline";
        viewModel.Sources[0].Path = Path.GetTempFileName();
        viewModel.Profiles.Add(new RunProfileDefinition { Name = "Beta" });
        viewModel.SelectedProfile = viewModel.Profiles[0];

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.Profiles.Count);
        Assert.Equal("Loaded 2 profile(s).", viewModel.StatusMessage);
        Assert.NotNull(viewModel.SelectedProfile);
        Assert.Equal("Beta", viewModel.SelectedProfile!.Name);

        File.Delete(viewModel.Sources[0].Path);
    }

    [Fact]
    public async Task RefreshCommand_handles_exception()
    {
        var service = new FakeDriftbusterService
        {
            ListProfilesHandler = _ => Task.FromException<RunProfileListResult>(new InvalidOperationException("boom")),
        };

        var viewModel = new RunProfilesViewModel(service);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("boom", viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task SaveCommand_invokes_backend_and_refreshes_profiles()
    {
        var baseline = Path.GetTempFileName();
        try
        {
            var savedNames = new System.Collections.Generic.List<string>();
            var service = new FakeDriftbusterService
            {
                SaveProfileHandler = (profile, _) =>
                {
                    savedNames.Add(profile.Name);
                    return Task.CompletedTask;
                },
                ListProfilesHandler = _ => Task.FromResult(new RunProfileListResult
                {
                    Profiles = new[] { new RunProfileDefinition { Name = "saved" } },
                }),
            };

            var viewModel = new RunProfilesViewModel(service)
            {
                ProfileName = "saved",
            };
            viewModel.Sources[0].Path = baseline;

            await viewModel.SaveCommand.ExecuteAsync(null);

            savedNames.Should().ContainSingle().Which.Should().Be("saved");
            viewModel.StatusMessage.Should().NotBeNull();
            viewModel.StatusMessage!.ToLowerInvariant().Should().Contain("profile");
            viewModel.Profiles.Should().ContainSingle(p => p.Name == "saved");
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public async Task SaveCommand_handles_exception()
    {
        var service = new FakeDriftbusterService
        {
            SaveProfileHandler = (_, _) => Task.FromException(new InvalidOperationException("save failed")),
        };

        var baseline = Path.GetTempFileName();
        try
        {
            var viewModel = new RunProfilesViewModel(service)
            {
                ProfileName = "sample",
            };
            viewModel.Sources[0].Path = baseline;

            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.Equal("save failed", viewModel.StatusMessage);
            Assert.False(viewModel.IsBusy);
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public async Task RunCommand_handles_exception_and_clears_results()
    {
        var service = new FakeDriftbusterService
        {
            RunProfileHandler = (_, _, _) => Task.FromException<RunProfileRunResult>(new InvalidOperationException("run failed")),
        };

        var baseline = Path.GetTempFileName();
        try
        {
            var viewModel = new RunProfilesViewModel(service)
            {
                ProfileName = "run",
            };
            viewModel.Sources[0].Path = baseline;

            await viewModel.RunCommand.ExecuteAsync(null);

            Assert.False(viewModel.HasRunResults);
            Assert.Equal("run failed", viewModel.StatusMessage);
            Assert.False(viewModel.IsBusy);
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public async Task PrepareOfflineCollectorAsync_validates_inputs_before_invoking_backend()
    {
        var service = new FakeDriftbusterService();
        var viewModel = new RunProfilesViewModel(service);

        await viewModel.PrepareOfflineCollectorAsync(string.Empty);
        Assert.Equal("Select an output path for the offline collector.", viewModel.StatusMessage);

        await viewModel.PrepareOfflineCollectorAsync("collector.zip");
        Assert.Equal("Configure a valid profile before preparing an offline collector.", viewModel.StatusMessage);
    }

    [Fact]
    public void HandleBaselineChanged_ensures_single_baseline()
    {
        var service = new FakeDriftbusterService();
        var viewModel = new RunProfilesViewModel(service);
        var baselinePath = Path.GetTempFileName();
        try
        {
            var baseline = viewModel.Sources[0];
            baseline.Path = baselinePath;

            viewModel.AddSourceCommand.Execute(null);
            var secondary = viewModel.Sources[1];
            secondary.Path = baselinePath;

            baseline.IsBaseline = false;
            viewModel.Sources.Count(source => source.IsBaseline).Should().Be(1);

            secondary.IsBaseline = true;
            viewModel.Sources.Count(source => source.IsBaseline).Should().Be(1);
            secondary.IsBaseline.Should().BeTrue();

            secondary.IsBaseline = false;
            viewModel.Sources.Count(source => source.IsBaseline).Should().Be(1);
        }
        finally
        {
            File.Delete(baselinePath);
        }
    }

    [Fact]
    public void GlobValidation_sets_errors_for_missing_base_directory()
    {
        var service = new FakeDriftbusterService();
        var viewModel = new RunProfilesViewModel(service)
        {
            ProfileName = "glob",
        };

        viewModel.Sources[0].Path = "C:/missing/*.json";
        Assert.Equal("Glob base directory not found.", viewModel.Sources[0].Error);

        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        viewModel.Sources[0].Path = Path.Combine(tempDir.FullName, "*.json");
        Assert.Null(viewModel.Sources[0].Error);

        Directory.Delete(tempDir.FullName);
    }

    [Fact]
    public void LoadProfileCommand_applies_profile_definition()
    {
        var service = new FakeDriftbusterService();
        var viewModel = new RunProfilesViewModel(service)
        {
            ProfileName = "initial",
        };

        var profile = new RunProfileDefinition
        {
            Name = "Loaded",
            Description = "  description ",
            Sources = new[] { "/baseline.txt", "/other.txt" },
            Options = new Dictionary<string, string>
            {
                ["Key"] = "Value",
            },
            SecretScanner = new SecretScannerOptions
            {
                IgnoreRules = new[] { " R1 ", "R1" },
                IgnorePatterns = new[] { "P" },
            },
        };

        viewModel.LoadProfileCommand.Execute(profile);

        viewModel.ProfileName.Should().Be("Loaded");
        viewModel.ProfileDescription.Should().Be("  description ");
        viewModel.Sources.Count.Should().Be(2);
        viewModel.Sources[0].IsBaseline.Should().BeTrue();
        viewModel.Sources[1].Path.Should().Be("/other.txt");
        viewModel.Options.Should().ContainSingle(option => option.Key == "Key" && option.Value == "Value");
        viewModel.SecretScannerSummary.Should().Contain("Ignored rules: 1, patterns: 1");
    }

    [Fact]
    public void OptionCommands_add_and_remove_entries()
    {
        var viewModel = new RunProfilesViewModel(new FakeDriftbusterService());

        viewModel.Options.Should().BeEmpty();
        viewModel.AddOptionCommand.Execute(null);
        viewModel.AddOptionCommand.Execute(null);
        viewModel.Options.Count.Should().Be(2);

        viewModel.Options[0].Key = "alpha";
        viewModel.Options[0].Value = "beta";

        viewModel.RemoveOptionCommand.Execute(viewModel.Options[1]);
        viewModel.Options.Should().ContainSingle();
    }

    [Fact]
    public void RemoveSourceCommand_reassigns_baseline()
    {
        var viewModel = new RunProfilesViewModel(new FakeDriftbusterService());

        var baselineFile = Path.GetTempFileName();
        var secondaryFile = Path.GetTempFileName();

        try
        {
            viewModel.Sources[0].Path = baselineFile;
            viewModel.AddSourceCommand.Execute(null);
            viewModel.Sources[1].Path = secondaryFile;
            viewModel.Sources[1].IsBaseline = true;

            viewModel.RemoveSourceCommand.Execute(viewModel.Sources[1]);

            viewModel.Sources.Should().ContainSingle();
            viewModel.Sources[0].IsBaseline.Should().BeTrue();
        }
        finally
        {
            File.Delete(baselineFile);
            File.Delete(secondaryFile);
        }
    }

    [Fact]
    public void OpenOutputCommand_uses_process_override()
    {
        var viewModel = new RunProfilesViewModel(new FakeDriftbusterService());
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        try
        {
            var tempFile = Path.GetTempFileName();
            viewModel.Sources[0].Path = tempFile;
            viewModel.ProfileName = "open";
            string? fileName = null;
            string? arguments = null;
            viewModel.ProcessStarterOverride = info =>
            {
                fileName = info.FileName;
                arguments = info.Arguments;
                return null;
            };

            viewModel.RunResults.Add(new RunProfilesViewModel.RunResultEntry("source", "dest", 1, "hash"));
            viewModel.OutputDirectory = tempDir.FullName;
            viewModel.OpenOutputCommand.CanExecute(null).Should().BeTrue();
            viewModel.OpenOutputCommand.Execute(null);

            fileName.Should().NotBeNull();
            var expected = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : OperatingSystem.IsLinux() ? "xdg-open" : tempDir.FullName;
            fileName.Should().Be(expected);
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            {
                arguments.Should().BeNullOrEmpty();
            }
            else
            {
                arguments.Should().NotBeNull();
            }
        }
        finally
        {
            foreach (var source in viewModel.Sources.ToArray())
            {
                if (!string.IsNullOrWhiteSpace(source.Path) && File.Exists(source.Path))
                {
                    File.Delete(source.Path);
                }
            }

            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
