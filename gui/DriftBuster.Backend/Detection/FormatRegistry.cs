using System.Text;

namespace DriftBuster.Backend.Detection;

/// <summary>
/// Insertion-ordered plugin registry with unique plugin names, plus the byte-level text sniffing and decoding
/// helpers shared by every plugin.
/// </summary>
public sealed class FormatRegistry
{
    private static readonly byte[] BomUtf8 = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] BomUtf16Le = [0xFF, 0xFE];
    private static readonly byte[] BomUtf16Be = [0xFE, 0xFF];

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16Le = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    private readonly List<IFormatPlugin> _plugins = [];
    private readonly Lock _gate = new();

    /// <summary>The process-wide registry that built-in plugins register into.</summary>
    public static FormatRegistry Default { get; } = new();

    /// <summary>
    /// Registers <paramref name="plugin"/>. Registering the same instance twice is ignored; a different plugin
    /// declaring an existing name throws <see cref="ArgumentException"/> so collisions surface at start-up.
    /// </summary>
    public void Register(IFormatPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (_gate)
        {
            if (!EnsureUnique(plugin))
            {
                return;
            }

            _plugins.Add(plugin);
        }
    }

    /// <summary>A snapshot of the registered plugins in registration order.</summary>
    public IReadOnlyList<IFormatPlugin> GetPlugins(bool readOnly = true)
    {
        lock (_gate)
        {
            // Both paths return a fresh snapshot; readOnly only picks the collection type.
            return readOnly ? _plugins.ToArray() : _plugins.ToList();
        }
    }

    /// <summary>An ordered summary of registered plugins for manual auditing.</summary>
    public IReadOnlyList<OrderedDictionary<string, object?>> RegistrySummary()
    {
        var plugins = GetPlugins();
        var summary = new List<OrderedDictionary<string, object?>>(plugins.Count);
        for (var index = 0; index < plugins.Count; index++)
        {
            var plugin = plugins[index];
            var type = plugin.GetType();
            summary.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = plugin.Name,
                ["version"] = plugin.Version,
                ["priority"] = plugin.Priority,
                ["order"] = index,
                ["module"] = type.Namespace ?? string.Empty,
                ["qualname"] = QualifiedName(type),
            });
        }

        return summary;
    }

    /// <summary>Plugin names mapped to their declared versions.</summary>
    public IReadOnlyDictionary<string, string> PluginVersions()
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var plugin in GetPlugins())
        {
            versions[plugin.Name] = plugin.Version;
        }

        return versions;
    }

    private static string QualifiedName(Type type)
    {
        var full = type.FullName ?? type.Name;
        var ns = type.Namespace;
        return string.IsNullOrEmpty(ns) ? full : full[(ns.Length + 1)..];
    }

    private bool EnsureUnique(IFormatPlugin plugin)
    {
        foreach (var existing in _plugins)
        {
            if (ReferenceEquals(existing, plugin))
            {
                return false;
            }

            if (string.Equals(existing.Name, plugin.Name, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"A plugin named '{plugin.Name}' is already registered: {existing.GetType().FullName}",
                    nameof(plugin));
            }
        }

        return true;
    }

    // Utility helpers shared across plugins.

    private static bool IsAsciiWhitelisted(byte value) => (value >= 32 && value <= 126) || value == 9 || value == 10 || value == 13;

    /// <summary>Removes a UTF-8, UTF-16 LE or UTF-16 BE byte order mark, reporting whether one was present.</summary>
    internal static (ReadOnlyMemory<byte> Stripped, bool HadBom) StripBom(ReadOnlyMemory<byte> sample)
    {
        foreach (var bom in new[] { BomUtf8, BomUtf16Le, BomUtf16Be })
        {
            if (sample.Span.StartsWith(bom))
            {
                return (sample[bom.Length..], true);
            }
        }

        return (sample, false);
    }

    /// <summary>The share of bytes in the printable-ASCII, tab, LF and CR whitelist; empty input scores 1.0.</summary>
    internal static double AsciiRatio(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 1.0;
        }

        var matches = 0;
        foreach (var value in data)
        {
            if (IsAsciiWhitelisted(value))
            {
                matches++;
            }
        }

        return (double)matches / data.Length;
    }

    private static int CountZeros(ReadOnlySpan<byte> data) => data.Count((byte)0);

    private static (byte[] Even, byte[] Odd) Interleave(ReadOnlySpan<byte> data)
    {
        var even = new byte[(data.Length + 1) / 2];
        var odd = new byte[data.Length / 2];
        for (var index = 0; index < data.Length; index++)
        {
            if (index % 2 == 0)
            {
                even[index / 2] = data[index];
            }
            else
            {
                odd[index / 2] = data[index];
            }
        }

        return (even, odd);
    }

    /// <summary>
    /// True when the sample is empty, starts with a byte order mark, is at least <paramref name="threshold"/>
    /// whitelist ASCII, or looks like UTF-16 (one byte lane ASCII while the other lane is mostly NUL).
    /// </summary>
    public static bool LooksText(ReadOnlyMemory<byte> sample, double threshold = 0.90)
    {
        if (sample.IsEmpty)
        {
            return true;
        }

        var (strippedMemory, hadBom) = StripBom(sample);
        if (hadBom)
        {
            return true;
        }

        var stripped = strippedMemory.Span;
        if (AsciiRatio(stripped) >= threshold)
        {
            return true;
        }

        if (!stripped.IsEmpty && CountZeros(stripped) >= stripped.Length / 4)
        {
            var (even, odd) = Interleave(stripped);
            if (AsciiRatio(even) >= threshold && (double)CountZeros(odd) / Math.Max(odd.Length, 1) >= 0.6)
            {
                return true;
            }

            if (AsciiRatio(odd) >= threshold && (double)CountZeros(even) / Math.Max(even.Length, 1) >= 0.6)
            {
                return true;
            }
        }

        return false;
    }

    public static bool LooksText(byte[] sample, double threshold = 0.90)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return LooksText(sample.AsMemory(), threshold);
    }

    /// <summary>
    /// Decodes <paramref name="sample"/> trying, in order, the codec announced by a byte order mark, then strict
    /// utf-8, utf-16-le and utf-16-be, and finally latin-1; leading U+FEFF characters are stripped. Returns the text
    /// and the name of the codec that succeeded.
    /// </summary>
    public static (string Text, string Encoding) DecodeText(ReadOnlyMemory<byte> sample) => DecodeText(sample, Decode);

    /// <summary>
    /// Mirrors <c>bytes.decode(encoding, errors)</c>: with <c>errors</c> "strict" an undecodable input raises
    /// <see cref="DecoderFallbackException"/>; with "replace" it never does. Only the built-in decoder ships; the seam
    /// exists so a test can make every strict codec fail and observe the replace-mode latin-1 line.
    /// </summary>
    internal delegate string TextDecoder(string encoding, string errors, ReadOnlyMemory<byte> data);

    internal static (string Text, string Encoding) DecodeText(ReadOnlyMemory<byte> sample, TextDecoder decode)
    {
        var candidates = new List<string>(7);
        var span = sample.Span;
        if (span.StartsWith(BomUtf8))
        {
            candidates.Add("utf-8-sig");
        }

        if (span.StartsWith(BomUtf16Le))
        {
            candidates.Add("utf-16-le");
        }

        if (span.StartsWith(BomUtf16Be))
        {
            candidates.Add("utf-16-be");
        }

        candidates.AddRange(["utf-8", "utf-16-le", "utf-16-be", "latin-1"]);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var encoding in candidates)
        {
            if (!seen.Add(encoding))
            {
                continue;
            }

            string text;
            try
            {
                text = decode(encoding, "strict", sample);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            return (text.TrimStart('\uFEFF'), encoding);
        }

        return (decode("latin-1", "replace", sample), "latin-1");
    }

    public static (string Text, string Encoding) DecodeText(byte[] sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return DecodeText(sample.AsMemory());
    }

    // Every codec here decodes latin-1 losslessly, so "replace" only ever reaches the latin-1 arm, which cannot fail.
    private static string Decode(string encoding, string errors, ReadOnlyMemory<byte> data) => encoding switch
    {
        "utf-8-sig" => StrictUtf8.GetString(data.Span[BomUtf8.Length..]),
        "utf-8" => StrictUtf8.GetString(data.Span),
        "utf-16-le" => StrictUtf16Le.GetString(data.Span),
        "utf-16-be" => StrictUtf16Be.GetString(data.Span),
        "latin-1" => Encoding.Latin1.GetString(data.Span),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), $"{encoding} ({errors})", "Unsupported codec."),
    };
}
