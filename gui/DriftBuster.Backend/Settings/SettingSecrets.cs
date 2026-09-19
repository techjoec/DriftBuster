using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Settings;

/// <summary>
/// Decides which setting values are secrets, so the comparison can say whether they match without showing them: a setting
/// whose name reads as a credential (password, secret, token, API or access key, private key, credential), or whose
/// <c>name=value</c> or value matches one of the secret scanner's rules.
/// </summary>
internal static partial class SettingSecrets
{
    [GeneratedRegex(@"pass(word|wd|phrase)?\b|pwd|secret|token|api[-_ ]?key|access[-_ ]?key|private[-_ ]?key|credential|client[-_ ]?secret|sas[-_ ]?key|shared[-_ ]?key|account[-_ ]?key",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SensitiveName();

    public static bool IsSecret(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return false;
        }

        // The last path segment names the setting ("appSettings:ApiToken", "db.password", "[auth] client_secret").
        var name = LastSegment(key);
        if (SensitiveName().IsMatch(name))
        {
            return true;
        }

        var rules = SecretRules.Packaged.Rules;
        var pair = $"{name}={value}";
        return rules.Any(rule => PatternRegex.Search(rule.Pattern, pair, cancellationToken) is not null
            || PatternRegex.Search(rule.Pattern, value, cancellationToken) is not null);
    }

    private static string LastSegment(string key)
    {
        var cut = key.LastIndexOfAny(['.', ':', '/', ']', ' ']);
        var tail = cut >= 0 && cut + 1 < key.Length ? key[(cut + 1)..] : key;
        var at = tail.IndexOf('@', StringComparison.Ordinal);
        return at >= 0 ? tail[(at + 1)..] : tail;
    }
}
