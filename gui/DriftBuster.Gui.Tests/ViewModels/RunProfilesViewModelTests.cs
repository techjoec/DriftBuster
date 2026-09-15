using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Scheduling;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;
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
            Assert.Equal(new[] { "A", "B" }, results.Select(r => r.Source), StringComparer.Ordinal);
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

            await viewModel.RefreshCommand.ExecuteAsync(null);
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
    public void Schedule_window_fields_require_pairs_and_default_to_utc()
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
            schedule.Error.Should().BeNull("ScheduleWindow.from_dict reads a window without a time zone as UTC");
            schedule.ToDefinition().Window!.Timezone.Should().BeNull();

            schedule.WindowTimezone = "UTC";
            schedule.Error.Should().BeNull();
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public void Schedule_entries_validate_through_the_scheduler_spec_rules()
    {
        var viewModel = new RunProfilesViewModel(new FakeDriftbusterService());
        viewModel.AddScheduleCommand.Execute(null);

        var schedule = Assert.Single(viewModel.Schedules);
        schedule.Name = "nightly";
        schedule.Profile = "nightly";
        schedule.Every = "daily";
        schedule.Error.Should().Be("Unsupported interval fragment near: daily");

        schedule.Every = "24h";
        schedule.StartAt = "02:00";
        schedule.Error.Should().Be("Invalid isoformat string: '02:00'");

        schedule.StartAt = "2025-01-01T02:00:00Z";
        schedule.WindowStart = "25:00";
        schedule.WindowEnd = "17:00";
        schedule.WindowTimezone = "Mars/Olympus";
        schedule.Error.Should().Be("Unknown time zone: Mars/Olympus");

        schedule.WindowTimezone = "America/Chicago";
        schedule.Error.Should().Be("hour must be in 0..23");

        schedule.WindowStart = "08:00";
        schedule.Error.Should().BeNull();
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
    public void Removing_metadata_entries_clears_errors_and_stops_listening()
    {
        var viewModel = new RunProfilesViewModel(new FakeDriftbusterService());
        viewModel.AddScheduleCommand.Execute(null);

        var schedule = Assert.Single(viewModel.Schedules);
        schedule.AddMetadataCommand.Execute(null);
        var entry = schedule.Metadata.Should().ContainSingle().Subject;

        metadata_Key_causes_error();

        schedule.RemoveMetadataCommand.Execute(entry);
        schedule.Error.Should().BeNull();

        entry.Key = "Changed after removal";
        schedule.Error.Should().BeNull();

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
            Sources = new[] { new RunProfileSource("/baseline.txt"), new RunProfileSource("/other.txt") },
            Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
        // A loaded profile's lists are kept as loaded (the store has already read them with secret_option_values).
        viewModel.SecretScannerSummary.Should().Contain("Ignored rules: 2, patterns: 1");
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

    [Fact]
    public async Task Structured_source_fields_round_trip_through_save_and_load()
    {
        var baseline = Path.GetTempFileName();
        var secondary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        try
        {
            RunProfileDefinition? saved = null;
            var service = new FakeDriftbusterService
            {
                SaveProfileHandler = (profile, _) =>
                {
                    saved = profile;
                    return Task.CompletedTask;
                },
            };

            var viewModel = new RunProfilesViewModel(service) { ProfileName = "structured" };
            viewModel.Sources[0].Path = baseline;
            viewModel.AddSourceCommand.Execute(null);
            var second = viewModel.Sources[1];
            second.Path = secondary.FullName;
            second.Alias = "  logs ";
            second.Optional = true;
            second.Exclude = "*.tmp\r\n\ncache/* \na;b";
            Assert.Null(second.Error);

            await viewModel.SaveCommand.ExecuteAsync(null);

            saved.Should().NotBeNull();
            saved!.Sources.Should().HaveCount(2);
            saved.Sources[0].Path.Should().Be(baseline);
            saved.Sources[0].IsPathOnly.Should().BeTrue();
            saved.Sources[1].Path.Should().Be(secondary.FullName);
            saved.Sources[1].Alias.Should().Be("logs");
            saved.Sources[1].Optional.Should().BeTrue();
            saved.Sources[1].Exclude.Should().Equal("*.tmp", "cache/* ", "a;b");

            var reloaded = new RunProfilesViewModel(new FakeDriftbusterService());
            reloaded.LoadProfileCommand.Execute(saved);

            reloaded.Sources.Should().HaveCount(2);
            reloaded.Sources[0].Alias.Should().BeNull();
            reloaded.Sources[0].Optional.Should().BeFalse();
            reloaded.Sources[0].Exclude.Should().BeEmpty();
            reloaded.Sources[1].Alias.Should().Be("logs");
            reloaded.Sources[1].Optional.Should().BeTrue();
            reloaded.Sources[1].Exclude.Should().Be("*.tmp\ncache/* \na;b");
        }
        finally
        {
            File.Delete(baseline);
            Directory.Delete(secondary.FullName, recursive: true);
        }
    }
    [Fact]
    public void A_loaded_path_and_alias_save_as_loaded_until_edited()
    {
        var viewModel = new RunProfilesViewModel(new FakeDriftbusterService());
        viewModel.LoadProfileCommand.Execute(new RunProfileDefinition
        {
            Name = "loaded",
            Sources = new[] { new RunProfileSource(" /data/in ") { Alias = " logs " } },
        });
        var entry = viewModel.Sources[0];

        entry.ToSource().Path.Should().Be(" /data/in ");
        entry.ToSource().Alias.Should().Be(" logs ");
        entry.AliasDirectory.Should().Be("-logs-");

        // An edited value is stripped as str.strip strips it (U+001F is Python white space, not .NET's); an alias it empties is none.
        entry.Alias = "\u001f logs2 ";
        entry.Path = "\u001f/data/in ";
        entry.ToSource().Alias.Should().Be("logs2");
        entry.ToSource().Path.Should().Be("/data/in");
        entry.Alias = "\u001f ";
        entry.ToSource().Alias.Should().BeNull();
        entry.Alias = " logs ";
        entry.ToSource().Alias.Should().Be(" logs ");
    }

    // Loads each profile the store reads (as ListProfilesAsync hands it to the tab), saves it without edits, and runs the loaded and the
    // saved profile over the same tree: the collected files, the per-source summaries, metadata.json and profile.json must be identical.
    [Theory]
    [InlineData("{\"name\": \" logs \", \"sources\": [{\"path\": \"{a}\", \"alias\": \"x\"}, \"{b}\", \"{c}\"], \"baseline\": \"{b}\", \"options\": {\" k\": 1, \"A\": \"2\", \"a\": \"3\"}, \"secret_scanner\": {\"ignore_patterns\": [\" x\", \" x\"]}, \"description\": \" d \"}")]
    [InlineData("{\"name\": \"n\", \"sources\": [{\"path\": \"{a}\", \"alias\": \"x\"}, \"{b}\", \"{c}\"]}")]
    [InlineData("{\"name\": \"plain\", \"sources\": [\"{a}\", \"{b}\"], \"baseline\": \"{b}\", \"options\": {\"\": \"e\"}}")]
    public async Task A_loaded_profile_saved_without_edits_collects_exactly_as_loaded(string payload)
    {
        var root = Directory.CreateTempSubdirectory("driftbuster-gui-round-trip-");
        try
        {
            string Tree(string name, string content)
            {
                var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "tree", name)).FullName;
                File.WriteAllText(Path.Combine(directory, name + ".txt"), content);
                return directory;
            }

            var text = payload.Replace("{a}", Tree("a", "password = Hunter12345\n"), StringComparison.Ordinal)
                .Replace("{b}", Tree("b", "b"), StringComparison.Ordinal).Replace("{c}", Tree("c", "c"), StringComparison.Ordinal);
            DriftBuster.Backend.Infrastructure.PythonJson.TryLoads(text, out var parsed).Should().BeTrue();
            var loaded = RunProfile.FromDict(parsed).ToDefinition();

            RunProfileDefinition? saved = null;
            var service = new FakeDriftbusterService { SaveProfileHandler = (profile, _) => { saved = profile; return Task.CompletedTask; } };
            var viewModel = new RunProfilesViewModel(service);
            viewModel.LoadProfileCommand.Execute(loaded);
            viewModel.SaveCommand.CanExecute(null).Should().BeTrue();
            await viewModel.SaveCommand.ExecuteAsync(null);
            saved.Should().NotBeNull();

            string Run(RunProfileDefinition definition, string side)
            {
                var baseDir = Directory.CreateDirectory(Path.Combine(root.FullName, side)).FullName;
                var result = RunProfileExecutor.ExecuteProfile(RunProfile.FromDefinition(definition), baseDir, "t", TestContext.Current.CancellationToken);
                var files = result.Files.Select(file => $"{Path.GetRelativePath(result.OutputDir, file.Destination)} {file.Size} {file.Sha256}");
                var sources = result.Sources.Select(source => $"{source.Path}|{source.Directory}|{source.Skipped}|{string.Join(",", source.Matched)}");
                var profileDirectory = Path.GetDirectoryName(Path.GetDirectoryName(result.OutputDir))!;
                var record = string.Join("\n", files.Concat(sources))
                    + File.ReadAllText(Path.Combine(result.OutputDir, "metadata.json")) + File.ReadAllText(Path.Combine(profileDirectory, "profile.json"))
                    + Path.GetFileName(profileDirectory);
                return record.Replace(baseDir, "<base>", StringComparison.Ordinal);
            }

            Run(saved!, "saved").Should().Be(Run(loaded, "loaded"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_loaded_baseline_saves_as_the_spelling_of_its_source()
    {
        var root = Directory.CreateTempSubdirectory("driftbuster-gui-baseline-");
        try
        {
            var plain = Path.Combine(root.FullName, "a");
            var spaced = plain + " ";
            var upper = Path.Combine(root.FullName, "A");
            Directory.CreateDirectory(plain);
            Directory.CreateDirectory(spaced);
            Directory.CreateDirectory(upper);
            RunProfileDefinition? saved = null;
            var service = new FakeDriftbusterService
            {
                SaveProfileHandler = (profile, _) =>
                {
                    saved = profile;
                    return Task.CompletedTask;
                },
            };
            var viewModel = new RunProfilesViewModel(service);
            viewModel.LoadProfileCommand.Execute(new RunProfileDefinition
            {
                Name = "spaced",
                Sources = new[] { new RunProfileSource(plain), new RunProfileSource(spaced) },
                Baseline = spaced,
            });

            viewModel.Sources.Select(source => source.IsBaseline).Should().Equal(false, true);
            viewModel.SaveCommand.CanExecute(null).Should().BeTrue();
            await viewModel.SaveCommand.ExecuteAsync(null);

            saved!.Baseline.Should().Be(spaced);
            saved.Sources.Select(source => source.Path).Should().Equal(plain, spaced);
            var validate = () => RunProfileStore.ValidateProfile(RunProfile.FromDefinition(saved));
            validate.Should().NotThrow();

            // An edited baseline is saved stripped as str.strip strips it (U+001F included), the same text as its source.
            viewModel.Sources[1].Path = "\u001f" + spaced;
            await viewModel.SaveCommand.ExecuteAsync(null);
            saved!.Baseline.Should().Be(plain);
            saved.Sources[1].Path.Should().Be(plain);

            // The baseline is matched exactly, never ignoring case.
            viewModel.LoadProfileCommand.Execute(new RunProfileDefinition
            {
                Name = "case",
                Sources = new[] { new RunProfileSource(upper), new RunProfileSource(plain) },
                Baseline = plain,
            });
            viewModel.Sources.Select(source => source.IsBaseline).Should().Equal(false, true);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Schedules_are_not_saved_over_a_manifest_that_did_not_load()
    {
        var baseline = Path.GetTempFileName();
        try
        {
            var saves = 0;
            var failing = true;
            var service = new FakeDriftbusterService
            {
                ListSchedulesHandler = _ => failing
                    ? Task.FromException<ScheduleListResult>(new InvalidOperationException("Failed to parse schedules"))
                    : Task.FromResult(new ScheduleListResult { Schedules = new[] { new ScheduleDefinition { Name = "n", Profile = "p", Every = "1h" } } }),
                SaveSchedulesHandler = (_, _) =>
                {
                    saves++;
                    return Task.CompletedTask;
                },
            };
            var viewModel = new RunProfilesViewModel(service) { ProfileName = "p" };
            viewModel.Sources[0].Path = baseline;

            // Before any load, and after a load that failed, the cards are not the manifest: they are never written over it.
            await viewModel.RunCommand.ExecuteAsync(null);
            viewModel.StatusMessage.Should().Be("Run complete. No files were copied.", "there were no cards to leave unsaved");
            AddScheduleCard(viewModel, "stale");
            await viewModel.RunCommand.ExecuteAsync(null);
            viewModel.StatusMessage.Should().Be("Run complete. No files were copied. Schedule cards were not saved: the schedule manifest has not been loaded.");

            await viewModel.RefreshCommand.ExecuteAsync(null);
            viewModel.Schedules.Should().BeEmpty();
            viewModel.StatusMessage.Should().Be("The schedule manifest could not be loaded, so schedules are not saved until it loads: Failed to parse schedules");

            AddScheduleCard(viewModel, "new");
            await viewModel.SaveCommand.ExecuteAsync(null);
            viewModel.Schedules.Should().BeEmpty("the refresh after saving failed to load the manifest again");
            AddScheduleCard(viewModel, "new");
            await viewModel.RunCommand.ExecuteAsync(null);
            viewModel.StatusMessage.Should().EndWith("Schedule cards were not saved: the schedule manifest has not been loaded.");
            await viewModel.PrepareOfflineCollectorAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip"));
            viewModel.StatusMessage.Should().EndWith("Schedule cards were not saved: the schedule manifest has not been loaded.");
            saves.Should().Be(0);

            failing = false;
            await viewModel.RefreshCommand.ExecuteAsync(null);
            viewModel.Schedules.Select(schedule => schedule.Name).Should().Equal("n");
            await viewModel.SaveCommand.ExecuteAsync(null);
            saves.Should().Be(1);
            await viewModel.RunCommand.ExecuteAsync(null);
            saves.Should().Be(2);
            viewModel.StatusMessage.Should().Be("Run complete. No files were copied.");
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    private static void AddScheduleCard(RunProfilesViewModel viewModel, string name)
    {
        viewModel.AddScheduleCommand.Execute(null);
        viewModel.Schedules[^1].Name = name;
        viewModel.Schedules[^1].Every = "1h";
    }

    [Fact]
    public async Task Loaded_exclude_patterns_the_editor_cannot_spell_are_saved_unchanged()
    {
        var baseline = Path.GetTempFileName();
        try
        {
            RunProfileDefinition? saved = null;
            var service = new FakeDriftbusterService
            {
                SaveProfileHandler = (profile, _) =>
                {
                    saved = profile;
                    return Task.CompletedTask;
                },
            };
            var patterns = new[] { " lead", "trail ", "semi;colon", "line\nbreak", string.Empty };
            var viewModel = new RunProfilesViewModel(service);
            viewModel.LoadProfileCommand.Execute(new RunProfileDefinition
            {
                Name = "patterns",
                Sources = new[] { new RunProfileSource(baseline) { Exclude = patterns } },
                Baseline = baseline,
            });

            await viewModel.SaveCommand.ExecuteAsync(null);

            saved!.Sources[0].Exclude.Should().Equal(patterns);

            viewModel.Sources[0].Exclude += "\n*.bak";
            await viewModel.SaveCommand.ExecuteAsync(null);

            saved!.Sources[0].Exclude.Should().Equal(" lead", "trail ", "semi;colon", "line", "break", "*.bak");
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public async Task An_optional_source_whose_path_is_missing_can_be_saved_and_run()
    {
        var baseline = Path.GetTempFileName();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.log");
        var missingGlob = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "*.log");
        try
        {
            RunProfileDefinition? ran = null;
            var service = new FakeDriftbusterService
            {
                RunProfileHandler = (profile, _, _) =>
                {
                    ran = profile;
                    return Task.FromResult(new RunProfileRunResult { Profile = profile });
                },
            };
            var viewModel = new RunProfilesViewModel(service) { ProfileName = "optional" };
            viewModel.Sources[0].Path = baseline;
            viewModel.AddSourceCommand.Execute(null);
            viewModel.AddSourceCommand.Execute(null);
            var file = viewModel.Sources[1];
            var glob = viewModel.Sources[2];
            file.Path = missing;
            glob.Path = missingGlob;
            file.Error.Should().Be("Path does not exist.");
            glob.Error.Should().Be("Glob base directory not found.");
            viewModel.SaveCommand.CanExecute(null).Should().BeFalse();

            file.Optional = true;
            glob.Optional = true;

            file.Error.Should().BeNull();
            glob.Error.Should().BeNull();
            viewModel.SaveCommand.CanExecute(null).Should().BeTrue();
            viewModel.RunCommand.CanExecute(null).Should().BeTrue();
            await viewModel.RunCommand.ExecuteAsync(null);
            ran!.Sources.Select(source => source.Optional).Should().Equal(false, true, true);

            file.Optional = false;
            file.Error.Should().Be("Path does not exist.");
            viewModel.SaveCommand.CanExecute(null).Should().BeFalse();
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public void Two_sources_whose_aliases_name_the_same_directory_are_flagged()
    {
        var baseline = Path.GetTempFileName();
        var other = Path.GetTempFileName();
        try
        {
            var viewModel = new RunProfilesViewModel(new FakeDriftbusterService()) { ProfileName = "aliases" };
            viewModel.Sources[0].Path = baseline;
            viewModel.AddSourceCommand.Execute(null);
            viewModel.Sources[1].Path = other;
            viewModel.Sources[0].Alias = "app logs";
            viewModel.SaveCommand.CanExecute(null).Should().BeTrue();

            viewModel.Sources[1].Alias = "app-logs";

            viewModel.Sources[0].Error.Should().Be("Another source uses the same alias.");
            viewModel.Sources[1].Error.Should().Be("Another source uses the same alias.");
            viewModel.SaveCommand.CanExecute(null).Should().BeFalse();

            viewModel.Sources[1].Alias = "  ";

            viewModel.Sources[0].Error.Should().BeNull();
            viewModel.Sources[1].Error.Should().BeNull();
            viewModel.SaveCommand.CanExecute(null).Should().BeTrue();
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(other);
        }
    }
    [Fact]
    public async Task A_schedule_card_with_a_numeric_interval_keeps_its_json_value_and_can_be_saved()
    {
        var baseline = Path.GetTempFileName();
        try
        {
            var captured = new List<ScheduleDefinition>();
            var service = new FakeDriftbusterService
            {
                ScheduleListResponse = new ScheduleListResult
                {
                    Schedules = new[]
                    {
                        new ScheduleDefinition
                        {
                            Name = "n",
                            Profile = "p",
                            Every = "90",
                            EveryValue = 90,
                            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["count"] = "5" },
                            MetadataValues = new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = 5 },
                        },
                    },
                },
                SaveProfileHandler = (_, _) => Task.CompletedTask,
                SaveSchedulesHandler = (schedules, _) =>
                {
                    captured = schedules.ToList();
                    return Task.CompletedTask;
                },
            };
            var viewModel = new RunProfilesViewModel(service) { ProfileName = "p" };
            await viewModel.RefreshCommand.ExecuteAsync(null);
            viewModel.Sources[0].Path = baseline;

            var card = Assert.Single(viewModel.Schedules);
            card.Error.Should().BeNull();
            viewModel.SaveCommand.CanExecute(null).Should().BeTrue();
            await viewModel.SaveCommand.ExecuteAsync(null);

            var saved = Assert.Single(captured);
            saved.EveryValue.Should().Be(90);
            saved.MetadataValues!["count"].Should().Be(5);
            ScheduleStore.ValidationError(saved).Should().BeNull();

            // Saving refreshes the cards from the service.
            var reloaded = Assert.Single(viewModel.Schedules);
            reloaded.Error.Should().BeNull();
            reloaded.Every = "120";
            reloaded.Error.Should().Be("Unsupported interval fragment near: 120");
            viewModel.SaveCommand.CanExecute(null).Should().BeFalse();
        }
        finally
        {
            File.Delete(baseline);
        }
    }
}
