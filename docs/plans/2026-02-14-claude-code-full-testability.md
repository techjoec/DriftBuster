# Claude Code Full Testability Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make DriftBuster fully testable by Claude Code with a single command, filling test coverage gaps in .NET services, models, converters, and ViewModels.

**Architecture:** Create a `scripts/test-all.sh` entry point that handles the `__JOE_PROFILE_ENV` env guard and runs both Python and .NET test suites with coverage gates. Then fill the identified .NET test gaps: `PerformanceProfile` (100 LOC, 0 tests), `FileJsonLogger` (99 LOC, 0 tests), backend model JSON round-trips (36 DTOs, 0 tests), converter edge cases, and ViewModel severity/command coverage. All new tests use xUnit with `[Theory]`/`[InlineData]` where parameterization reduces duplication.

**Tech Stack:** xUnit 2.9.3, Avalonia.Headless.XUnit, AwesomeAssertions, coverlet, pytest, coverage.py

**Important project conventions:**
- `gui/DriftBuster.Gui.Tests/Usings.cs` provides `global using AwesomeAssertions;` and `global using Xunit;` -- do NOT add explicit usings for these in test files
- `Directory.Build.props` at repo root sets `net10.0`, nullable, implicit usings, `Meziantou.Analyzer`
- The `__JOE_PROFILE_ENV` env var guards `.profile` in child shells, blocking `dotnet` discovery. Per CLAUDE.md: `unset __JOE_PROFILE_ENV && bash --login -c 'dotnet ...'`
- All backend models use explicit `[JsonPropertyName("snake_case")]` attributes -- serializer options should use defaults (no naming policy), letting attributes control the wire format
- Coverage threshold: 90% line for both Python and .NET (hard gate)
- Run `dotnet format <project> --verify-no-changes` before committing .NET code; if it fails, run `dotnet format <project>` first to fix

---

### Task 1: Create `scripts/test-all.sh` unified entry point

**Files:**
- Create: `scripts/test-all.sh`

**Step 1: Write the script**

Create `scripts/test-all.sh` with this exact content:

```bash
#!/usr/bin/env bash
set -euo pipefail
# Unified test runner for Claude Code: handles __JOE_PROFILE_ENV guard,
# runs Python + .NET tests with coverage gates, prints summary.
#
# Usage: ./scripts/test-all.sh

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

FAILURES=0

# Activate local venv if present
if [[ -f ".venv/bin/activate" ]]; then
    # shellcheck disable=SC1091
    source .venv/bin/activate
fi

# Helper: run dotnet in a clean shell (handles __JOE_PROFILE_ENV guard)
run_dotnet() {
    unset __JOE_PROFILE_ENV 2>/dev/null || true
    bash --login -c "dotnet $*"
}

echo "=== Python tests (90% coverage gate) ==="
if coverage run --source=src/driftbuster -m pytest -q; then
    if coverage report --fail-under=90; then
        coverage json -o coverage.json
        echo "PASS: Python tests + coverage"
    else
        echo "FAIL: Python coverage below 90%"
        FAILURES=$((FAILURES + 1))
    fi
else
    echo "FAIL: Python tests"
    FAILURES=$((FAILURES + 1))
fi

echo ""
echo "=== Python compliance guardrail ==="
if python -m scripts.coverage_watch --python-json coverage.json 2>/dev/null; then
    echo "PASS: Compliance watch"
else
    echo "FAIL: Compliance watch"
    FAILURES=$((FAILURES + 1))
fi

echo ""
echo "=== Python lint (ruff) ==="
if ruff check src; then
    echo "PASS: ruff"
else
    echo "FAIL: ruff"
    FAILURES=$((FAILURES + 1))
fi

echo ""
echo "=== .NET tests (90% line coverage gate) ==="
if run_dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj \
    -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total \
    --collect:\"XPlat Code Coverage\" \
    --results-directory artifacts/coverage-dotnet -v minimal; then
    echo "PASS: .NET tests"
else
    echo "FAIL: .NET tests"
    FAILURES=$((FAILURES + 1))
fi

echo ""
echo "=== .NET format check ==="
for proj in gui/DriftBuster.Backend/DriftBuster.Backend.csproj \
            gui/DriftBuster.Gui/DriftBuster.Gui.csproj \
            gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj; do
    if run_dotnet format "$proj" --verify-no-changes; then
        echo "PASS: dotnet format $proj"
    else
        echo "FAIL: dotnet format $proj"
        FAILURES=$((FAILURES + 1))
    fi
done

echo ""
echo "========================================"
if [[ $FAILURES -eq 0 ]]; then
    echo "ALL CHECKS PASSED"
    exit 0
else
    echo "FAILED: $FAILURES check(s)"
    exit 1
fi
```

**Step 2: Make it executable and verify**

Run: `chmod +x scripts/test-all.sh && head -5 scripts/test-all.sh`
Expected: Shows shebang, set, and comment lines.

**Step 3: Commit**

```bash
git add scripts/test-all.sh
git commit -m "feat: add scripts/test-all.sh unified test runner for Claude Code"
```

---

### Task 2: Add `PerformanceProfileTests.cs`

**Files:**
- Create: `gui/DriftBuster.Gui.Tests/Services/PerformanceProfileTests.cs`

**Context:** `gui/DriftBuster.Gui/Services/PerformanceProfile.cs` (100 LOC) has zero tests. It handles env var parsing (`DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD`, `DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION`), threshold validation, force-override logic, and fluent builders. All pure logic, no Avalonia dependency. The `ShouldVirtualize` method returns `ForceVirtualizationOverride` if set, otherwise compares `itemCount >= VirtualizationThreshold`. The default threshold is 400. Constructor rejects `threshold <= 0`. `ShouldVirtualize` rejects `itemCount < 0`.

**Step 1: Write the test file**

Create `gui/DriftBuster.Gui.Tests/Services/PerformanceProfileTests.cs` with this exact content:

```csharp
using System;

using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Tests.Services;

public sealed class PerformanceProfileTests : IDisposable
{
    private readonly string? _origThreshold;
    private readonly string? _origForce;

    public PerformanceProfileTests()
    {
        _origThreshold = Environment.GetEnvironmentVariable("DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD");
        _origForce = Environment.GetEnvironmentVariable("DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD", _origThreshold);
        Environment.SetEnvironmentVariable("DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION", _origForce);
    }

    [Fact]
    public void Default_threshold_is_400()
    {
        var profile = new PerformanceProfile();
        profile.VirtualizationThreshold.Should().Be(400);
        profile.ForceVirtualizationOverride.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_rejects_non_positive_threshold(int threshold)
    {
        Action act = () => new PerformanceProfile(threshold);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(399, false)]
    [InlineData(400, true)]
    [InlineData(401, true)]
    public void ShouldVirtualize_compares_against_threshold(int itemCount, bool expected)
    {
        var profile = new PerformanceProfile();
        profile.ShouldVirtualize(itemCount).Should().Be(expected);
    }

    [Fact]
    public void ShouldVirtualize_rejects_negative_count()
    {
        var profile = new PerformanceProfile();
        Action act = () => profile.ShouldVirtualize(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1000, true)]
    [InlineData(false, 0, false)]
    [InlineData(false, 1000, false)]
    public void ForceOverride_takes_precedence_over_threshold(bool force, int itemCount, bool expected)
    {
        var profile = new PerformanceProfile(forceVirtualizationOverride: force);
        profile.ShouldVirtualize(itemCount).Should().Be(expected);
    }

    [Fact]
    public void WithThreshold_returns_new_profile_preserving_force()
    {
        var original = new PerformanceProfile(200, true);
        var updated = original.WithThreshold(500);
        updated.VirtualizationThreshold.Should().Be(500);
        updated.ForceVirtualizationOverride.Should().Be(true);
    }

    [Fact]
    public void WithForceOverride_returns_new_profile_preserving_threshold()
    {
        var original = new PerformanceProfile(200);
        var updated = original.WithForceOverride(false);
        updated.VirtualizationThreshold.Should().Be(200);
        updated.ForceVirtualizationOverride.Should().Be(false);
    }

    [Theory]
    [InlineData("100", null, 100, null)]
    [InlineData(null, "true", 400, true)]
    [InlineData(null, "1", 400, true)]
    [InlineData(null, "yes", 400, true)]
    [InlineData(null, "on", 400, true)]
    [InlineData(null, "false", 400, false)]
    [InlineData(null, "0", 400, false)]
    [InlineData(null, "no", 400, false)]
    [InlineData(null, "off", 400, false)]
    [InlineData("abc", null, 400, null)]
    [InlineData("-5", null, 400, null)]
    [InlineData("0", null, 400, null)]
    [InlineData(null, "maybe", 400, null)]
    [InlineData("", "", 400, null)]
    public void FromEnvironment_parses_variables(string? threshold, string? force, int expectedThreshold, bool? expectedForce)
    {
        Environment.SetEnvironmentVariable("DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD", threshold);
        Environment.SetEnvironmentVariable("DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION", force);

        var profile = PerformanceProfile.FromEnvironment();

        profile.VirtualizationThreshold.Should().Be(expectedThreshold);
        profile.ForceVirtualizationOverride.Should().Be(expectedForce);
    }
}
```

**Step 2: Format and run tests**

Run: `unset __JOE_PROFILE_ENV && bash --login -c 'dotnet format gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj && dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter PerformanceProfileTests -v minimal'`
Expected: Format succeeds (or no changes), all tests PASS.

**Step 3: Commit**

```bash
git add gui/DriftBuster.Gui.Tests/Services/PerformanceProfileTests.cs
git commit -m "test: add PerformanceProfile unit tests with Theory coverage"
```

---

### Task 3: Add `FileJsonLoggerTests.cs`

**Files:**
- Create: `gui/DriftBuster.Gui.Tests/Services/FileJsonLoggerTests.cs`

**Context:** `gui/DriftBuster.Gui/Services/FileJsonLogger.cs` (99 LOC) has zero tests. It implements `ILogger<T>`. Key behaviors: constructor rejects null/empty/whitespace path; `IsEnabled` returns false for Trace/Debug, true for Information+; `Log` writes structured JSON to file (overwrites, not appends); creates parent directories; includes exception when present; `BeginScope` returns a non-null `NullScope` singleton. Pure file I/O, no Avalonia dependency.

**Step 1: Write the test file**

Create `gui/DriftBuster.Gui.Tests/Services/FileJsonLoggerTests.cs` with this exact content:

```csharp
using System;
using System.IO;
using System.Text.Json;

using DriftBuster.Gui.Services;

using Microsoft.Extensions.Logging;

namespace DriftBuster.Gui.Tests.Services;

public sealed class FileJsonLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public FileJsonLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DriftbusterTests", "Logger", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_invalid_path(string? path)
    {
        Action act = () => new FileJsonLogger<FileJsonLoggerTests>(path!);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(LogLevel.Trace, false)]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Information, true)]
    [InlineData(LogLevel.Warning, true)]
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Critical, true)]
    public void IsEnabled_filters_below_information(LogLevel level, bool expected)
    {
        var logger = new FileJsonLogger<FileJsonLoggerTests>(Path.Combine(_tempDir, "test.json"));
        logger.IsEnabled(level).Should().Be(expected);
    }

    [Fact]
    public void Log_writes_structured_json_to_file()
    {
        var path = Path.Combine(_tempDir, "entry.json");
        var logger = new FileJsonLogger<FileJsonLoggerTests>(path);

        logger.LogInformation(new EventId(42, "TestEvent"), "Hello {Name}", "World");

        File.Exists(path).Should().BeTrue();
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("level").GetString().Should().Be("Information");
        root.GetProperty("eventId").GetInt32().Should().Be(42);
        root.GetProperty("eventName").GetString().Should().Be("TestEvent");
        root.GetProperty("category").GetString().Should().Contain(nameof(FileJsonLoggerTests));
        root.GetProperty("message").GetString().Should().Contain("Hello");
    }

    [Fact]
    public void Log_below_information_does_not_write()
    {
        var path = Path.Combine(_tempDir, "debug.json");
        var logger = new FileJsonLogger<FileJsonLoggerTests>(path);

        logger.LogDebug("Should not write");

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Log_creates_parent_directory_if_missing()
    {
        var path = Path.Combine(_tempDir, "nested", "deep", "output.json");
        var logger = new FileJsonLogger<FileJsonLoggerTests>(path);

        logger.LogWarning("test");

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Log_overwrites_previous_entry()
    {
        var path = Path.Combine(_tempDir, "overwrite.json");
        var logger = new FileJsonLogger<FileJsonLoggerTests>(path);

        logger.LogInformation("first");
        logger.LogInformation("second");

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("message").GetString().Should().Contain("second");
    }

    [Fact]
    public void Log_includes_exception_when_present()
    {
        var path = Path.Combine(_tempDir, "exception.json");
        var logger = new FileJsonLogger<FileJsonLoggerTests>(path);
        var ex = new InvalidOperationException("boom");

        logger.Log(LogLevel.Error, new EventId(0), "fail", ex, (s, e) => $"{s}: {e?.Message}");

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("exception").GetString().Should().Contain("boom");
    }

    [Fact]
    public void BeginScope_returns_non_null_disposable()
    {
        var logger = new FileJsonLogger<FileJsonLoggerTests>(Path.Combine(_tempDir, "scope.json"));
        using var scope = logger.BeginScope("test-scope");
        scope.Should().NotBeNull();
    }
}
```

**Step 2: Format and run tests**

Run: `unset __JOE_PROFILE_ENV && bash --login -c 'dotnet format gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj && dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter FileJsonLoggerTests -v minimal'`
Expected: Format succeeds, all tests PASS.

**Step 3: Commit**

```bash
git add gui/DriftBuster.Gui.Tests/Services/FileJsonLoggerTests.cs
git commit -m "test: add FileJsonLogger unit tests covering all paths"
```

---

### Task 4: Add backend model JSON round-trip tests

**Files:**
- Create: `gui/DriftBuster.Gui.Tests/Backend/ModelSerializationTests.cs`

**Context:** 36 backend model classes in `gui/DriftBuster.Backend/Models/` (883 LOC total) are DTOs with `[JsonPropertyName("snake_case")]` attributes but zero serialization tests. All properties have explicit `[JsonPropertyName]` attributes, so use default `JsonSerializerOptions` (no naming policy). Some properties are `[JsonIgnore]` (e.g. `DiffResult.RawJson`, `DiffResult.SanitizedJson`, `HuntResult.RawJson`) -- these won't round-trip and should not be asserted. Property names to use: `DiffChangeSummary.BeforeLines` / `.AfterLines` / `.AddedLines` / `.RemovedLines` / `.ChangedLines` / `.DiffDigest`.

**Step 1: Write the test file**

Create `gui/DriftBuster.Gui.Tests/Backend/ModelSerializationTests.cs` with this exact content:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;

using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.Tests.Backend;

public sealed class ModelSerializationTests
{
    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json)!;
    }

    [Fact]
    public void DiffResult_round_trips_with_defaults()
    {
        var original = new DiffResult();
        var result = RoundTrip(original);
        result.Versions.Should().BeEmpty();
        result.Comparisons.Should().BeEmpty();
    }

    [Fact]
    public void DiffResult_round_trips_with_values()
    {
        var original = new DiffResult
        {
            Versions = new[] { "v1.json", "v2.json" },
            Comparisons = new[]
            {
                new DiffComparison
                {
                    From = "v1.json",
                    To = "v2.json",
                    UnifiedDiff = "--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new",
                },
            },
        };

        var result = RoundTrip(original);
        result.Versions.Should().Equal("v1.json", "v2.json");
        result.Comparisons.Should().HaveCount(1);
        result.Comparisons[0].From.Should().Be("v1.json");
        result.Comparisons[0].UnifiedDiff.Should().Contain("+new");
    }

    [Fact]
    public void DiffResult_json_ignore_properties_do_not_round_trip()
    {
        var original = new DiffResult
        {
            RawJson = "raw-content",
            SanitizedJson = "sanitized-content",
        };

        var result = RoundTrip(original);
        result.RawJson.Should().BeEmpty();
        result.SanitizedJson.Should().BeEmpty();
    }

    [Fact]
    public void HuntResult_round_trips()
    {
        var original = new HuntResult
        {
            Directory = "/tmp/scan",
            Pattern = "*.config",
            Count = 2,
            Hits = new[]
            {
                new HuntHit
                {
                    Path = "/tmp/scan/web.config",
                    RelativePath = "web.config",
                    LineNumber = 10,
                    Excerpt = "connectionString=secret",
                    Rule = new HuntRuleSummary { Name = "connection-string", Description = "Database connection strings" },
                },
            },
        };

        var result = RoundTrip(original);
        result.Directory.Should().Be("/tmp/scan");
        result.Pattern.Should().Be("*.config");
        result.Count.Should().Be(2);
        result.Hits.Should().HaveCount(1);
        result.Hits[0].LineNumber.Should().Be(10);
        result.Hits[0].Rule.Name.Should().Be("connection-string");
    }

    [Fact]
    public void HuntResult_json_ignore_does_not_round_trip()
    {
        var original = new HuntResult { RawJson = "raw-hunt" };
        var result = RoundTrip(original);
        result.RawJson.Should().BeEmpty();
    }

    [Fact]
    public void ServerScanPlan_round_trips_with_nested_objects()
    {
        var original = new ServerScanPlan
        {
            HostId = "host-01",
            Label = "Production",
            Scope = ServerScanScope.CustomRoots,
            Roots = new[] { "/etc/app", "/var/lib/app" },
            ThrottleSeconds = 2.5,
            CachedAt = new DateTimeOffset(2026, 2, 14, 12, 0, 0, TimeSpan.Zero),
            Baseline = new ServerScanBaselinePreference { IsPreferred = true, Priority = 1, Role = "primary" },
            Export = new ServerScanExportOptions
            {
                IncludeCatalog = true,
                IncludeDrilldown = false,
                IncludeDiffs = true,
                IncludeSummary = true,
            },
        };

        var result = RoundTrip(original);
        result.HostId.Should().Be("host-01");
        result.Roots.Should().Equal("/etc/app", "/var/lib/app");
        result.ThrottleSeconds.Should().Be(2.5);
        result.CachedAt.Should().NotBeNull();
        result.Baseline.IsPreferred.Should().BeTrue();
        result.Baseline.Priority.Should().Be(1);
        result.Baseline.Role.Should().Be("primary");
        result.Export.IncludeDrilldown.Should().BeFalse();
    }

    [Fact]
    public void RunProfileDefinition_round_trips_with_options_dict()
    {
        var original = new RunProfileDefinition
        {
            Name = "nightly-scan",
            Description = "Full nightly configuration scan",
            Sources = new[] { "/etc/app", "/opt/service" },
            Baseline = "/etc/app",
            Options = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sample_size"] = "256000",
                ["content_type"] = "auto",
            },
            SecretScanner = new SecretScannerOptions
            {
                IgnoreRules = new[] { "example-token" },
                IgnorePatterns = new[] { "*.test" },
            },
        };

        var result = RoundTrip(original);
        result.Name.Should().Be("nightly-scan");
        result.Description.Should().Be("Full nightly configuration scan");
        result.Sources.Should().Equal("/etc/app", "/opt/service");
        result.Baseline.Should().Be("/etc/app");
        result.Options.Should().ContainKey("sample_size");
        result.Options["content_type"].Should().Be("auto");
        result.SecretScanner.IgnoreRules.Should().Equal("example-token");
        result.SecretScanner.IgnorePatterns.Should().Equal("*.test");
    }

    [Fact]
    public void ScheduleDefinition_round_trips_with_window_and_metadata()
    {
        var original = new ScheduleDefinition
        {
            Name = "daily-check",
            Profile = "nightly-scan",
            Every = "1d",
            StartAt = "02:00",
            Window = new ScheduleWindowDefinition
            {
                Start = "01:00",
                End = "05:00",
                Timezone = "UTC",
            },
            Tags = new[] { "production", "critical" },
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["team"] = "infra" },
        };

        var result = RoundTrip(original);
        result.Name.Should().Be("daily-check");
        result.Every.Should().Be("1d");
        result.StartAt.Should().Be("02:00");
        result.Window.Should().NotBeNull();
        result.Window!.Start.Should().Be("01:00");
        result.Window.End.Should().Be("05:00");
        result.Window.Timezone.Should().Be("UTC");
        result.Tags.Should().Equal("production", "critical");
        result.Metadata.Should().ContainKey("team");
    }

    [Fact]
    public void SecretScannerOptions_defaults_to_empty_arrays()
    {
        var original = new SecretScannerOptions();
        var result = RoundTrip(original);
        result.IgnoreRules.Should().BeEmpty();
        result.IgnorePatterns.Should().BeEmpty();
    }

    [Fact]
    public void ServerScanResponse_round_trips_with_defaults()
    {
        var original = new ServerScanResponse();
        var result = RoundTrip(original);
        result.Version.Should().BeEmpty();
        result.Results.Should().BeEmpty();
        result.Catalog.Should().BeEmpty();
        result.Drilldown.Should().BeEmpty();
        result.Summary.Should().BeNull();
    }

    [Fact]
    public void DiffChangeSummary_round_trips_line_counts()
    {
        var original = new DiffChangeSummary
        {
            BeforeDigest = "abc123",
            AfterDigest = "def456",
            DiffDigest = "diff789",
            BeforeLines = 100,
            AfterLines = 110,
            AddedLines = 15,
            RemovedLines = 5,
            ChangedLines = 10,
        };

        var result = RoundTrip(original);
        result.BeforeDigest.Should().Be("abc123");
        result.AfterDigest.Should().Be("def456");
        result.DiffDigest.Should().Be("diff789");
        result.BeforeLines.Should().Be(100);
        result.AfterLines.Should().Be(110);
        result.AddedLines.Should().Be(15);
        result.RemovedLines.Should().Be(5);
        result.ChangedLines.Should().Be(10);
    }

    [Fact]
    public void ScanProgress_round_trips()
    {
        var ts = new DateTimeOffset(2026, 2, 14, 12, 30, 0, TimeSpan.Zero);
        var original = new ScanProgress
        {
            HostId = "srv-01",
            Status = ServerScanStatus.Running,
            Message = "Scanning /etc",
            Timestamp = ts,
        };

        var result = RoundTrip(original);
        result.HostId.Should().Be("srv-01");
        result.Message.Should().Be("Scanning /etc");
        result.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void ServerScanExportOptions_defaults_all_true()
    {
        var original = new ServerScanExportOptions();
        var result = RoundTrip(original);
        result.IncludeCatalog.Should().BeTrue();
        result.IncludeDrilldown.Should().BeTrue();
        result.IncludeDiffs.Should().BeTrue();
        result.IncludeSummary.Should().BeTrue();
    }

    [Fact]
    public void ServerScanBaselinePreference_defaults()
    {
        var original = new ServerScanBaselinePreference();
        var result = RoundTrip(original);
        result.IsPreferred.Should().BeFalse();
        result.Priority.Should().Be(0);
        result.Role.Should().Be("auto");
    }
}
```

**Step 2: Format and run tests**

Run: `unset __JOE_PROFILE_ENV && bash --login -c 'dotnet format gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj && dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter ModelSerializationTests -v minimal'`
Expected: Format succeeds, all tests PASS.

**Step 3: Commit**

```bash
git add gui/DriftBuster.Gui.Tests/Backend/ModelSerializationTests.cs
git commit -m "test: add backend model JSON round-trip tests for 13 DTOs"
```

---

### Task 5: Replace `ActivityEntryViewModelTests.cs` with full Theory coverage

**Files:**
- Replace: `gui/DriftBuster.Gui.Tests/ViewModels/ActivityEntryViewModelTests.cs`

**Context:** `gui/DriftBuster.Gui/ViewModels/ActivityEntryViewModel.cs` has properties: `Id` (Guid), `Severity`, `Summary`, `Detail`, `Timestamp`, `Category`, `TimestampText`, `SeverityLabel`, `ClipboardText`, `IsError` (true when Error), `IsWarning` (true when Warning), `IsExport` (true when Export category). The existing test file has 1 test covering only Error and Info severity. `ActivitySeverity` enum has: Info, Success, Warning, Error. `ActivityCategory` enum has: General, Export.

**Step 1: Write the complete replacement file**

Replace the entire content of `gui/DriftBuster.Gui.Tests/ViewModels/ActivityEntryViewModelTests.cs` with:

```csharp
using System;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class ActivityEntryViewModelTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2025, 10, 21, 5, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ActivitySeverity.Error, true, false)]
    [InlineData(ActivitySeverity.Warning, false, true)]
    [InlineData(ActivitySeverity.Info, false, false)]
    [InlineData(ActivitySeverity.Success, false, false)]
    public void Severity_flags_reflect_enum_value(ActivitySeverity severity, bool isError, bool isWarning)
    {
        var entry = new ActivityEntryViewModel(severity, "msg", "", FixedTimestamp, ActivityCategory.General);
        entry.IsError.Should().Be(isError);
        entry.IsWarning.Should().Be(isWarning);
        entry.SeverityLabel.Should().Be(severity.ToString());
    }

    [Theory]
    [InlineData(ActivityCategory.General, false)]
    [InlineData(ActivityCategory.Export, true)]
    public void IsExport_reflects_category(ActivityCategory category, bool expected)
    {
        var entry = new ActivityEntryViewModel(ActivitySeverity.Info, "msg", "", FixedTimestamp, category);
        entry.IsExport.Should().Be(expected);
    }

    [Fact]
    public void Id_is_unique_per_instance()
    {
        var a = new ActivityEntryViewModel(ActivitySeverity.Info, "a", "", FixedTimestamp, ActivityCategory.General);
        var b = new ActivityEntryViewModel(ActivitySeverity.Info, "b", "", FixedTimestamp, ActivityCategory.General);
        a.Id.Should().NotBe(Guid.Empty);
        a.Id.Should().NotBe(b.Id);
    }

    [Fact]
    public void TimestampText_contains_year()
    {
        var entry = new ActivityEntryViewModel(ActivitySeverity.Info, "msg", "", FixedTimestamp, ActivityCategory.General);
        entry.TimestampText.Should().Contain("2025");
    }

    [Fact]
    public void ClipboardText_includes_detail_when_present()
    {
        var entry = new ActivityEntryViewModel(ActivitySeverity.Error, "Scan failed", "Permission denied", FixedTimestamp, ActivityCategory.General);
        entry.ClipboardText.Should().Contain("Scan failed");
        entry.ClipboardText.Should().Contain("Permission denied");
    }

    [Fact]
    public void ClipboardText_omits_detail_when_empty()
    {
        var entry = new ActivityEntryViewModel(ActivitySeverity.Info, "Completed", "", FixedTimestamp, ActivityCategory.General);
        entry.ClipboardText.Should().Contain("Completed");
        entry.ClipboardText.Should().NotContain(Environment.NewLine);
    }
}
```

**Step 2: Format and run tests**

Run: `unset __JOE_PROFILE_ENV && bash --login -c 'dotnet format gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj && dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter ActivityEntryViewModelTests -v minimal'`
Expected: Format succeeds, all tests PASS.

**Step 3: Commit**

```bash
git add gui/DriftBuster.Gui.Tests/ViewModels/ActivityEntryViewModelTests.cs
git commit -m "test: expand ActivityEntryViewModel tests with Theory for all severities and categories"
```

---

### Task 6: Replace `SecretScannerSettingsViewModelTests.cs` with edge case coverage

**Files:**
- Replace: `gui/DriftBuster.Gui.Tests/ViewModels/SecretScannerSettingsViewModelTests.cs`

**Context:** `gui/DriftBuster.Gui/ViewModels/SecretScannerSettingsViewModel.cs` key behaviors: constructor `Load()` skips whitespace-only entries from options, ensures minimum 1 entry per collection; `AddRuleCommand`/`AddPatternCommand` append empty entries and update canExecute; `RemoveRuleCommand`/`RemovePatternCommand` guard against null parameter and re-add empty entry if collection drops to 0; `BuildResult()` trims, deduplicates, and sorts. The `SecretScannerOptions.IgnoreRules`/`IgnorePatterns` arrays default to `Array.Empty<string>()` but could be null at runtime. Existing file has 2 tests.

**Step 1: Write the complete replacement file**

Replace the entire content of `gui/DriftBuster.Gui.Tests/ViewModels/SecretScannerSettingsViewModelTests.cs` with:

```csharp
using DriftBuster.Backend.Models;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class SecretScannerSettingsViewModelTests
{
    [Fact]
    public void BuildResult_trims_and_deduplicates_entries()
    {
        var options = new SecretScannerOptions
        {
            IgnoreRules = new[] { " rule1 ", "rule2", "", "rule2" },
            IgnorePatterns = new[] { "  pattern1", "pattern2", string.Empty },
        };

        var viewModel = new SecretScannerSettingsViewModel(options);
        viewModel.AddRuleCommand.Execute(null);
        viewModel.IgnoreRules[^1].Value = "rule1";
        viewModel.AddPatternCommand.Execute(null);
        viewModel.IgnorePatterns[^1].Value = "pattern2";

        var result = viewModel.BuildResult();

        result.IgnoreRules.Should().Equal("rule1", "rule2");
        result.IgnorePatterns.Should().Equal("pattern1", "pattern2");
    }

    [Fact]
    public void Remove_commands_keep_at_least_one_entry()
    {
        var viewModel = new SecretScannerSettingsViewModel(new SecretScannerOptions());

        viewModel.IgnoreRules.Should().HaveCount(1);
        viewModel.RemoveRuleCommand.CanExecute(viewModel.IgnoreRules[0]).Should().BeFalse();

        viewModel.AddRuleCommand.Execute(null);
        viewModel.IgnoreRules.Should().HaveCount(2);
        viewModel.RemoveRuleCommand.CanExecute(viewModel.IgnoreRules[1]).Should().BeTrue();
        viewModel.RemoveRuleCommand.Execute(viewModel.IgnoreRules[1]);
        viewModel.IgnoreRules.Should().HaveCount(1);

        viewModel.IgnorePatterns.Should().HaveCount(1);
        viewModel.AddPatternCommand.Execute(null);
        viewModel.IgnorePatterns.Should().HaveCount(2);
        viewModel.RemovePatternCommand.Execute(viewModel.IgnorePatterns[0]);
        viewModel.IgnorePatterns.Should().HaveCount(1);
    }

    [Fact]
    public void Constructor_handles_null_arrays_in_options()
    {
        var options = new SecretScannerOptions
        {
            IgnoreRules = null!,
            IgnorePatterns = null!,
        };

        var viewModel = new SecretScannerSettingsViewModel(options);

        viewModel.IgnoreRules.Should().HaveCount(1);
        viewModel.IgnoreRules[0].Value.Should().BeEmpty();
        viewModel.IgnorePatterns.Should().HaveCount(1);
        viewModel.IgnorePatterns[0].Value.Should().BeEmpty();
    }

    [Fact]
    public void RemoveRule_ignores_null_parameter()
    {
        var viewModel = new SecretScannerSettingsViewModel(new SecretScannerOptions());
        viewModel.AddRuleCommand.Execute(null);
        var countBefore = viewModel.IgnoreRules.Count;

        viewModel.RemoveRuleCommand.Execute(null);

        viewModel.IgnoreRules.Should().HaveCount(countBefore);
    }

    [Fact]
    public void RemovePattern_ignores_null_parameter()
    {
        var viewModel = new SecretScannerSettingsViewModel(new SecretScannerOptions());
        viewModel.AddPatternCommand.Execute(null);
        var countBefore = viewModel.IgnorePatterns.Count;

        viewModel.RemovePatternCommand.Execute(null);

        viewModel.IgnorePatterns.Should().HaveCount(countBefore);
    }

    [Fact]
    public void Add_remove_cycle_maintains_minimum_one_entry()
    {
        var viewModel = new SecretScannerSettingsViewModel(new SecretScannerOptions());

        viewModel.AddRuleCommand.Execute(null);
        viewModel.AddRuleCommand.Execute(null);
        viewModel.IgnoreRules.Should().HaveCount(3);

        viewModel.RemoveRuleCommand.Execute(viewModel.IgnoreRules[2]);
        viewModel.RemoveRuleCommand.Execute(viewModel.IgnoreRules[1]);
        viewModel.RemoveRuleCommand.Execute(viewModel.IgnoreRules[0]);
        viewModel.IgnoreRules.Should().HaveCountGreaterOrEqualTo(1);
    }

    [Fact]
    public void BuildResult_returns_empty_arrays_when_all_entries_blank()
    {
        var viewModel = new SecretScannerSettingsViewModel(new SecretScannerOptions());
        viewModel.IgnoreRules[0].Value = "   ";
        viewModel.IgnorePatterns[0].Value = "";

        var result = viewModel.BuildResult();

        result.IgnoreRules.Should().BeEmpty();
        result.IgnorePatterns.Should().BeEmpty();
    }
}
```

**Step 2: Format and run tests**

Run: `unset __JOE_PROFILE_ENV && bash --login -c 'dotnet format gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj && dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter SecretScannerSettingsViewModelTests -v minimal'`
Expected: Format succeeds, all tests PASS.

**Step 3: Commit**

```bash
git add gui/DriftBuster.Gui.Tests/ViewModels/SecretScannerSettingsViewModelTests.cs
git commit -m "test: expand SecretScannerSettingsViewModel edge cases (null arrays, remove cycles)"
```

---

### Task 7: Replace `StringNotEmptyConverterTests.cs` with Theory coverage

**Files:**
- Replace: `gui/DriftBuster.Gui.Tests/Converters/StringNotEmptyConverterTests.cs`

**Context:** `gui/DriftBuster.Gui/Converters/StringNotEmptyConverter.cs` returns `!string.IsNullOrWhiteSpace(text)` for string inputs and `false` for non-string inputs. `ConvertBack` throws `NotSupportedException`. Existing file has 2 tests but misses empty string `""` as distinct from `null`/whitespace, and non-string input types. Note: `Usings.cs` provides global `using Xunit;` and `using AwesomeAssertions;` so `[Theory]`/`[InlineData]` are available without explicit imports.

**Step 1: Write the complete replacement file**

Replace the entire content of `gui/DriftBuster.Gui.Tests/Converters/StringNotEmptyConverterTests.cs` with:

```csharp
using System;
using System.Globalization;

using DriftBuster.Gui.Converters;

namespace DriftBuster.Gui.Tests.Converters;

public sealed class StringNotEmptyConverterTests
{
    [Theory]
    [InlineData("hello", true)]
    [InlineData("  x  ", true)]
    [InlineData("value", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Convert_handles_string_variants(string input, bool expected)
    {
        StringNotEmptyConverter.Instance.Convert(input, typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(expected);
    }

    [Fact]
    public void Convert_returns_false_for_null()
    {
        StringNotEmptyConverter.Instance.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(false);
    }

    [Fact]
    public void Convert_returns_false_for_non_string_types()
    {
        StringNotEmptyConverter.Instance.Convert(42, typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(false);
        StringNotEmptyConverter.Instance.Convert(true, typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(false);
    }

    [Fact]
    public void ConvertBack_is_not_supported()
    {
        Action act = () => StringNotEmptyConverter.Instance.ConvertBack(true, typeof(string), null, CultureInfo.InvariantCulture);
        act.Should().Throw<NotSupportedException>();
    }
}
```

**Step 2: Format and run tests**

Run: `unset __JOE_PROFILE_ENV && bash --login -c 'dotnet format gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj && dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --filter StringNotEmptyConverterTests -v minimal'`
Expected: Format succeeds, all tests PASS.

**Step 3: Commit**

```bash
git add gui/DriftBuster.Gui.Tests/Converters/StringNotEmptyConverterTests.cs
git commit -m "test: add Theory and non-string edge cases to StringNotEmptyConverter"
```

---

### Task 8: Run full test suite and verify coverage

**Files:**
- None (verification only)

**Step 1: Run the new unified test script**

Run: `./scripts/test-all.sh`
Expected: `ALL CHECKS PASSED` with 0 failures. If any check fails, fix it before proceeding.

**Step 2: Verify .NET test count increased**

Run: `unset __JOE_PROFILE_ENV && bash --login -c 'dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj -v minimal'`
Expected: Total tests should be approximately 230+ (was ~182 before this plan).

**Step 3: Verify Python tests still pass (no regressions)**

Run: `coverage run --source=src/driftbuster -m pytest -q && coverage report --fail-under=90`
Expected: All 537+ tests pass, coverage >= 90%.

---

## Summary

| Task | Action | File | Tests Added |
|------|--------|------|-------------|
| 1 | Create | `scripts/test-all.sh` | Tooling (single entry point) |
| 2 | Create | `gui/.../Services/PerformanceProfileTests.cs` | ~14 (all paths: constructor, threshold, force, env parsing) |
| 3 | Create | `gui/.../Services/FileJsonLoggerTests.cs` | ~8 (constructor, level filter, write, overwrite, exception, scope) |
| 4 | Create | `gui/.../Backend/ModelSerializationTests.cs` | ~13 (round-trips for 13 representative DTOs + defaults + JsonIgnore) |
| 5 | Replace | `gui/.../ViewModels/ActivityEntryViewModelTests.cs` | 7 (was 1; covers all 4 severities, 2 categories, clipboard) |
| 6 | Replace | `gui/.../ViewModels/SecretScannerSettingsViewModelTests.cs` | 7 (was 2; adds null arrays, null params, cycles, blank entries) |
| 7 | Replace | `gui/.../Converters/StringNotEmptyConverterTests.cs` | 4 (was 2; adds Theory, empty string, non-string types) |
| 8 | Verify | Full suite | Validation only |

**Total new test methods: ~50**
**Estimated coverage lift: ~1,100 LOC of previously untested production code**
