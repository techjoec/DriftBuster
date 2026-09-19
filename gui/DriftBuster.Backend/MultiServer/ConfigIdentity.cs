using System.Buffers;
using System.Globalization;
using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.MultiServer;

/// <summary>Config ids and display names.</summary>
/// <remarks>
/// The id is <c>slug(format)/slug(variant)/slug(relative path)</c> (variant only when present), so two apps' <c>web.config</c> on one
/// host keep separate ids. When a host still produces the same id twice, the later record gets <see cref="Disambiguate"/>'s
/// <c>@root{index}</c> suffix (index = zero-based position of its root in <see cref="MultiServerPlan.Roots"/>, missing roots counted,
/// so roots coming and going never rename others), then <c>.{n}</c> from 2. Slugs never hold '@' or '.', so suffixed ids never
/// collide with natural ones.
/// </remarks>
public static class ConfigIdentity
{
    /// <summary>Trimmed and lowered; every code point that is not a letter, number, '-', '_' or '/' becomes '-'.</summary>
    public static string Slugify(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = EngineText.Lower(EngineText.Strip(value));
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var offset = 0;
        while (offset < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out var consumed) != OperationStatus.Done)
            {
                // An unpaired surrogate is not alphanumeric.
                builder.Append('-');
                offset++;
                continue;
            }

            if (EngineText.IsAlnum(rune) || rune.Value is '-' or '_' or '/')
            {
                builder.Append(text, offset, consumed);
            }
            else
            {
                builder.Append('-');
            }

            offset += consumed;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Format from <c>catalog_format</c>, else the match's format name, else <c>config</c>; variant from a non-blank <c>catalog_variant</c>.
    /// When the relative path slugs to nothing the id is <c>slug(format)#sha1(fallback)[:12]</c>.
    /// </summary>
    public static string NormaliseConfigId(DetectionMatch match, string relativePosix)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(relativePosix);
        var metadata = match.Metadata;
        var formatId = metadata.Text("catalog_format") is { Length: > 0 } catalogFormat ? catalogFormat
            : string.IsNullOrEmpty(match.FormatName) ? "config" : match.FormatName;
        var slug = Slugify(relativePosix);
        if (slug.Length > 0)
        {
            var parts = new List<string> { Slugify(formatId) };
            if (metadata.Text("catalog_variant") is { } variant && variant.Trim().Length > 0)
            {
                parts.Add(Slugify(variant));
            }

            parts.Add(slug);
            return string.Join('/', parts.Where(part => part.Length > 0));
        }

        var fallback = relativePosix.Length > 0 ? relativePosix : (string.IsNullOrEmpty(match.PluginName) ? "config" : match.PluginName);
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(EncodeIgnoringErrors(fallback)));
        return $"{Slugify(formatId)}#{digest[..12]}";
    }

    /// <summary>
    /// <paramref name="configId"/> when free; otherwise <c>{configId}@root{rootIndex}</c>, then <c>.{n}</c> for n = 2, 3, ... until free.
    /// </summary>
    public static string Disambiguate(string configId, int rootIndex, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(configId);
        ArgumentNullException.ThrowIfNull(isTaken);
        if (!isTaken(configId))
        {
            return configId;
        }

        var suffixed = string.Create(CultureInfo.InvariantCulture, $"{configId}@root{rootIndex}");
        var candidate = suffixed;
        for (var attempt = 2; isTaken(candidate); attempt++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{suffixed}.{attempt}");
        }

        return candidate;
    }

    // UTF-8 with unpaired surrogates dropped.
    private static byte[] EncodeIgnoringErrors(string text)
    {
        var builder = new StringBuilder(text.Length);
        var offset = 0;
        while (offset < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(offset), out _, out var consumed) == OperationStatus.Done)
            {
                builder.Append(text, offset, consumed);
            }

            offset += consumed;
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
