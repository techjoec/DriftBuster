using System.Buffers;
using System.Globalization;
using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.MultiServer;

/// <summary>Config ids and display names: <c>_slugify</c>, <c>_normalise_config_id</c> and <c>_display_name</c>.</summary>
/// <remarks>
/// Plan fix a: Python chose the id from the first non-blank of <c>config_original_filename</c>, <c>config_role</c>,
/// <c>msbuild_kind</c> and <c>top_level_type</c> before the relative path, so two applications holding a <c>web.config</c> on
/// one host shared an id and the later file silently replaced the earlier one. The port always builds the id from the relative
/// posix path: <c>slug(format)/slug(variant)/slug(relative path)</c>, the variant part only when a variant is present. When two
/// records on one host still produce the same id (the same relative path under two roots, or two paths whose slugs coincide),
/// both are kept: the first keeps the id and a later one gets <see cref="Disambiguate"/>'s <c>@root{index}</c> suffix, where
/// the index is the zero-based position of its root in the plan's roots (<see cref="MultiServerPlan.Roots"/>, missing roots
/// counted, so a root appearing or disappearing never renames another root's ids), with <c>.{n}</c>, n from 2, appended when
/// that is taken too. Slugs never hold '@' or '.', so a suffixed id never equals a natural one. The display name is unchanged.
/// </remarks>
public static class ConfigIdentity
{
    /// <summary>
    /// <c>_slugify</c>: stripped and lowered, then every code point that is not <c>str.isalnum</c> (Unicode letters and numbers)
    /// and not '-', '_' or '/' becomes '-'.
    /// </summary>
    public static string Slugify(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = PythonText.Lower(PythonText.Strip(value));
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
                // An unpaired surrogate is a code point of its own to Python, and not alphanumeric.
                builder.Append('-');
                offset++;
                continue;
            }

            if (PythonText.IsAlnum(rune) || rune.Value is '-' or '_' or '/')
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
    /// The config id of <paramref name="match"/> found at <paramref name="relativePosix"/> (fix a): the format is
    /// <c>catalog_format</c>, else the match's format name, else <c>config</c>; the variant is a non-blank string
    /// <c>catalog_variant</c>. When the relative path slugs to nothing the id is <c>slug(format)#sha1(fallback)[:12]</c>.
    /// </summary>
    public static string NormaliseConfigId(DetectionMatch match, string relativePosix)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(relativePosix);
        var metadata = match.Metadata;
        var formatId = FirstTruthy(Get(metadata, "catalog_format"), match.FormatName) ?? "config";
        var slug = Slugify(relativePosix);
        if (slug.Length > 0)
        {
            var parts = new List<string> { Slugify(formatId) };
            if (Get(metadata, "catalog_variant") is string variant && PythonText.Strip(variant).Length > 0)
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

    /// <summary><c>_display_name</c>: <c>config_original_filename</c> stripped when it is a non-blank string, else the relative path.</summary>
    public static string DisplayName(IReadOnlyDictionary<string, object?>? metadata, string relativePosix)
    {
        ArgumentNullException.ThrowIfNull(relativePosix);
        if (Get(metadata, "config_original_filename") is string name)
        {
            var stripped = PythonText.Strip(name);
            if (stripped.Length > 0)
            {
                return stripped;
            }
        }

        return relativePosix;
    }

    /// <summary>
    /// <paramref name="configId"/> when <paramref name="isTaken"/> rejects it; otherwise <c>{configId}@root{rootIndex}</c>, then
    /// <c>{configId}@root{rootIndex}.{n}</c> for n = 2, 3, ... until one is free.
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

    private static object? Get(IReadOnlyDictionary<string, object?>? metadata, string key)
        => metadata is not null && metadata.TryGetValue(key, out var value) ? value : null;

    // str(a or b): the first value Python treats as true, spelled with str().
    private static string? FirstTruthy(object? first, string? second)
    {
        if (IsTruthy(first))
        {
            return PythonRepr.Str(first);
        }

        return string.IsNullOrEmpty(second) ? null : second;
    }

    internal static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        string text => text.Length > 0,
        int number => number != 0,
        long number => number != 0,
        double number => number != 0,
        System.Collections.ICollection collection => collection.Count > 0,
        _ => true,
    };

    // str.encode("utf-8", "ignore"): unpaired surrogates dropped.
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
