using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Settings;

namespace DriftBuster.Backend.Tests.Settings;

/// <summary>Scanned JSON: lenient for settings, strict for the canonical diff text.</summary>
public sealed class JsonSettingsTests
{
    private const string AppSettings = """
        {
          // comment
          "Logging": { "Level": "Warning", },
          "Cache": { "Minutes": 1.50, "Enabled": true, "Tags": [] },
          "Servers": ["a", "b",],
        }
        """;

    [Fact]
    public void Comments_and_trailing_commas_still_give_settings_with_numbers_as_written()
    {
        var settings = SettingsExtractor.Extract("json", "json", "﻿" + AppSettings, "h");

        settings.Mode.Should().Be(SettingsMode.Parsed);
        settings.Entries.Should().Equal(
            new SettingEntry("Logging.Level", "Warning"),
            new SettingEntry("Cache.Minutes", "1.50"),
            new SettingEntry("Cache.Enabled", "true"),
            new SettingEntry("Cache.Tags", "[]"),
            new SettingEntry("Servers[1]", "a"),
            new SettingEntry("Servers[2]", "b"));
    }

    [Fact]
    public void Canonical_json_sorts_keys_and_keeps_text_as_written()
    {
        Canonicaliser.CanonicaliseJson("""{"b": 1.50, "a": {"é": [2, "x"]}}""").Should().Be(
            "{\n  \"a\": {\n    \"é\": [\n      2,\n      \"x\"\n    ]\n  },\n  \"b\": 1.50\n}");
    }

    [Fact]
    public void Canonical_json_with_comments_is_compared_as_text()
    {
        Canonicaliser.CanonicaliseJson(AppSettings).Should().Contain("// comment");
    }
}
