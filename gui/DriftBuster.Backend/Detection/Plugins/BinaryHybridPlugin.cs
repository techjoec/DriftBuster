using System.Text.Json.Nodes;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

using Microsoft.Data.Sqlite;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects three hybrid payloads from the raw sample bytes: a SQLite database by its 16-byte header (the table
/// count read from the real file), a binary property list by its <c>bplist00</c> header (top-level keys read by
/// <see cref="BinaryPlist"/>), and Markdown whose text opens with a YAML front matter block fenced by <c>---</c>
/// lines. The text is re-derived from the sample when the caller passed none but the bytes look like text.
/// </summary>
public sealed class BinaryHybridPlugin : IFormatPlugin
{
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();
    private static readonly byte[] BplistMagic = "bplist00"u8.ToArray();

    public string Name => "binary-hybrid";

    public int Priority => 210;

    public string Version => "0.1.0";

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sample);
        return DetectSqlite(path, sample) ?? DetectBinaryPlist(sample) ?? DetectMarkdownFrontMatter(sample, text);
    }

    private DetectionMatch? DetectSqlite(string path, byte[] sample)
    {
        if (!sample.AsSpan().StartsWith(SqliteMagic))
        {
            return null;
        }

        var reasons = new List<string> { "Detected SQLite database header (SQLite format 3)" };
        var tableCount = CountSqliteTables(path);
        if (tableCount is not null)
        {
            reasons.Add($"Enumerated {tableCount} table(s) via sqlite3 pragma");
        }

        var suffix = PathText.SuffixLower(path);
        var metadata = new JsonObject
        {
            ["signature"] = "sqlite-format-3",
            ["table_count"] = tableCount,
            ["catalog_hint"] = suffix.TrimStart('.'),
        };
        return new DetectionMatch(Name, "embedded-sql-db", "generic", 0.98, reasons, metadata);
    }

    /// <summary>
    /// Table count of the real database file, opened read-only through a connection string (not a file URI, so names with
    /// <c>%XX</c>, <c>?</c> or <c>#</c> open correctly) with pooling off so the file is closed on dispose; null when missing or rejected.
    /// </summary>
    internal static int? CountSqliteTables(string path)
    {
        path = EnginePath.KernelPath(path);
        if (!File.Exists(path))
        {
            return null;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table'";
            return command.ExecuteScalar() is long count ? checked((int)count) : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private DetectionMatch? DetectBinaryPlist(byte[] sample)
    {
        if (!sample.AsSpan().StartsWith(BplistMagic))
        {
            return null;
        }

        var metadata = new JsonObject
        {
            ["signature"] = "bplist00",
        };
        var reasons = new List<string> { "Detected binary property list header (bplist00)" };
        object? payload;
        try
        {
            payload = BinaryPlist.Load(sample);
        }
        catch (BinaryPlist.DecodeException exc)
        {
            metadata["decode_error"] = new JsonObject
            {
                ["type"] = exc.ErrorType,
                ["message"] = exc.Message,
            };
            reasons.Add("Binary plist payload could not be decoded; recorded error metadata");
            return new DetectionMatch(Name, "plist", "xml-or-binary", 0.92, reasons, metadata);
        }

        metadata["top_level_keys"] = JsonNodes.Array(BinaryPlist.SortedKeys(payload).Select(JsonNodes.From));
        reasons.Add("Parsed binary property list via plistlib");
        return new DetectionMatch(Name, "plist", "xml-or-binary", 0.92, reasons, metadata);
    }

    private DetectionMatch? DetectMarkdownFrontMatter(byte[] sample, string? text)
    {
        var workingText = text;
        if (workingText is null && FormatRegistry.LooksText(sample))
        {
            workingText = FormatRegistry.DecodeText(sample).Text;
        }

        if (string.IsNullOrEmpty(workingText))
        {
            return null;
        }

        if (!TryMatchFrontMatter(workingText, out var blockStart, out var blockEnd, out var matchEnd))
        {
            return null;
        }

        var block = workingText[blockStart..blockEnd].Trim();
        // Distinct non-empty keys in code-point order.
        var keySet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in TextLines.SplitLines(block))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            if (key.Length > 0)
            {
                keySet.Add(key);
            }
        }

        var keys = keySet.ToList();
        keys.Sort(PathText.CompareCodePoints);
        var metadata = new JsonObject
        {
            ["front_matter_keys"] = JsonNodes.Strings(keys),
            ["has_body"] = workingText[matchEnd..].Trim().Length > 0,
        };
        var reasons = new List<string> { "Detected YAML front matter fenced with '---' markers" };
        if (keys.Count > 0)
        {
            reasons.Add("Extracted keys: " + string.Join(", ", keys.Take(5)));
        }

        return new DetectionMatch(Name, "markdown-config", "embedded-yaml-frontmatter", 0.8, reasons, metadata);
    }

    /// <summary>
    /// Front matter <c>^---\s*\n(.*?\n)---\s*\n</c> (dot matches newline) in one pass, without backtracking: the opening newline
    /// is the last one after the dashes that has a closer beyond it, and the block ends at the first such closer.
    /// </summary>
    internal static bool TryMatchFrontMatter(string text, out int blockStart, out int blockEnd, out int matchEnd)
    {
        ArgumentNullException.ThrowIfNull(text);
        (blockStart, blockEnd, matchEnd) = (0, 0, 0);
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            return false;
        }

        var closers = FrontMatterClosers(text);
        if (closers.Count == 0)
        {
            return false;
        }

        // The largest opening newline p with a closer at or past p + 2.
        var latestCloser = closers[^1].Start;
        var runEnd = SpaceRunEnd(text, 3);
        for (var p = runEnd - 1; p >= 3; p--)
        {
            if (text[p] != '\n' || latestCloser < p + 2)
            {
                continue;
            }

            var closer = closers.First(candidate => candidate.Start >= p + 2);
            (blockStart, blockEnd, matchEnd) = (p + 1, closer.Start, closer.End);
            return true;
        }

        return false;
    }

    // Every q with text[q - 1] == '\n' and "---\s*\n" matching at q, in ascending order, with that match's end.
    private static List<(int Start, int End)> FrontMatterClosers(string text)
    {
        var closers = new List<(int Start, int End)>();
        var newline = text.IndexOf('\n', 3);
        while (newline >= 0)
        {
            var q = newline + 1;
            if (text.AsSpan(q).StartsWith("---", StringComparison.Ordinal))
            {
                var runEnd = SpaceRunEnd(text, q + 3);
                var last = runEnd > q + 3 ? text.LastIndexOf('\n', runEnd - 1, runEnd - (q + 3)) : -1;
                if (last >= 0)
                {
                    closers.Add((q, last + 1));
                }
            }

            newline = text.IndexOf('\n', q);
        }

        return closers;
    }

    private static int SpaceRunEnd(string text, int offset)
    {
        while (offset < text.Length && char.IsWhiteSpace(text[offset]))
        {
            offset++;
        }

        return offset;
    }
}
