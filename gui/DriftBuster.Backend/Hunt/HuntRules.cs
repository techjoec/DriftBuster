namespace DriftBuster.Backend.Hunt;

/// <summary><c>driftbuster.hunt.default_rules()</c>.</summary>
public static class HuntRules
{
    private static readonly Lazy<IReadOnlyList<HuntRule>> DefaultRules = new(Build);

    /// <summary>The baseline rules for common dynamic settings, in Python's order.</summary>
    public static IReadOnlyList<HuntRule> Default => DefaultRules.Value;

    private static IReadOnlyList<HuntRule> Build() =>
    [
        new HuntRule(
            "server-name",
            "Potential hostnames, server names, or FQDN references",
            "server_name",
            ["server", "host"],
            [@"\b[a-z0-9_-]+\.(local|lan|corp|com|net|internal)\b"]),
        new HuntRule(
            "certificate-thumbprint",
            "Likely certificate thumbprints",
            "certificate_thumbprint",
            ["thumbprint", "certificate"],
            [@"\b[0-9a-f]{40}\b", @"\b[0-9a-f]{64}\b"]),
        new HuntRule(
            "version-number",
            "Version identifiers (semver style)",
            "version",
            ["version"],
            [@"\b\d+\.\d+\.\d+(?:\.\d+)?\b"]),
        // Fix e: Python spells the drive pattern r"[A-Za-z]:\\\\[\\w\\-\\.\\s]+" (escaped twice inside a raw string), which
        // needs two literal backslashes after the colon and so never matches a Windows path; this is the pattern it meant.
        new HuntRule(
            "install-path",
            "Suspicious installation or directory paths",
            "install_path",
            ["path", "install"],
            [@"[A-Za-z]:\\[\w\-\.\s]+", @"/opt/[\w\-\.]+"]),
        new HuntRule(
            "connection-string",
            "Connection string attribute assignments",
            "connection_string",
            patterns: [@"connectionstring\s*=\s*['""][^'""\n]+['""]"]),
        new HuntRule(
            "service-endpoint",
            "Service endpoint or base address assignments",
            "service_endpoint",
            patterns:
            [
                @"<endpoint\b[^>]*\baddress\s*=\s*['""][^'\""]+['""]",
                @"(endpoint|serviceurl|baseaddress)\s*=\s*['""][^'\""]+['""]",
                @"key\s*=\s*['""][^'\""]*(endpoint|serviceurl|baseaddress|address)[^'\""]*['""][^\n]*value\s*=\s*['""][^'\""]+['""]",
            ]),
        new HuntRule(
            "feature-flag",
            "Feature flag or toggle assignments",
            "feature_flag",
            patterns:
            [
                @"key\s*=\s*['""][^'\""]*(feature|flag|toggle)[^'\""]*['""][^\n]*value\s*=\s*['""][^'\""]+['""]",
                @"<feature\b[^>]*\b(enabled|value)\s*=\s*['""][^'\""]+['""]",
            ]),
    ];
}
