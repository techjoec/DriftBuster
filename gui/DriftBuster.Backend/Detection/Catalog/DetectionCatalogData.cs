namespace DriftBuster.Backend.Detection.Catalog;

/// <summary>The catalog classes in priority order; values mirror the Python catalog (version 0.0.3) plus the port's own plist, markdown-config, logstash-pipeline and hcl entries.</summary>
internal static class DetectionCatalogData
{
    private static readonly FormatClass RegistryExport = new FormatClass(
        Name: "RegistryExport",
        Slug: "registry-export",
        Priority: 10,
        DefaultSeverity: "high",
        SeverityHint: "Registry exports capture entire hive snapshots, including secrets, policy settings, and service fingerprints.",
        RemediationHints:
        [
            new RemediationHint(
                "registry-export-lockdown",
                "secrets",
                "Store exported hives in restricted evidence shares and rotate credentials referenced in the dump.",
                "docs/detection-types.md#registryexport"),
        ],
        References: ["docs/detection-types.md#registryexport"]);

    private static readonly FormatClass RegistryLive = new FormatClass(
        Name: "RegistryLive",
        Slug: "registry-live",
        Priority: 15,
        DefaultSeverity: "medium",
        DefaultVariant: "scan-definition",
        SeverityHint: "Registry scan definitions describe automated hive reads and target tokens that expose sensitive audit scope.",
        RemediationHints:
        [
            new RemediationHint(
                "registry-live-scope-review",
                "review",
                "Confirm monitoring tokens align with approved hosts and rotate any credentials referenced in the manifest.",
                "docs/detection-types.md#registrylive"),
        ],
        References: ["docs/detection-types.md#registrylive"]);

    private static readonly FormatClass StructuredConfigXml = new FormatClass(
        Name: "StructuredConfigXml",
        Slug: "structured-config-xml",
        Priority: 20,
        DefaultSeverity: "high",
        DefaultVariant: "web-or-app-config",
        Aliases: ["structured-config"],
        Subtypes:
        [
            new FormatSubtype("WebConfigXml", 21, Variant: "web-config", Severity: "high"),
            new FormatSubtype("AppConfigXml", 22, Variant: "app-config", Severity: "high"),
            new FormatSubtype("MachineConfigXml", 23, Variant: "machine-config", Severity: "high"),
            new FormatSubtype("WebConfigTransform", 24, Variant: "web-config-transform", Severity: "high"),
            new FormatSubtype("AppConfigTransform", 25, Variant: "app-config-transform", Severity: "high"),
            new FormatSubtype("MachineConfigTransform", 26, Variant: "machine-config-transform", Severity: "high"),
            new FormatSubtype("GenericConfigTransform", 27, Variant: "config-transform", Severity: "high"),
            new FormatSubtype("CustomConfigXml", 28, Variant: "custom-config-xml", Severity: "medium", Aliases: ["sample"]),
            new FormatSubtype("NLogConfigXml", 29, Variant: "nlog-config", Severity: "high"),
            new FormatSubtype("Log4NetConfigXml", 30, Variant: "log4net-config", Severity: "high"),
            new FormatSubtype("SerilogConfigXml", 31, Variant: "serilog-config", Severity: "high"),
        ],
        SeverityHint:
            "Application configuration files expose secrets, connection strings, and runtime policy toggles "
            + "that impact production systems.",
        RemediationHints:
        [
            new RemediationHint(
                "structured-config-rotate-secrets",
                "secrets",
                "Rotate credentials stored in configuration sections and confirm transforms match approved deployment scopes.",
                "docs/detection-types.md#structuredconfigxml"),
            new RemediationHint(
                "structured-config-hardening",
                "hardening",
                "Review debug switches and permissive runtime settings before promoting captured configs to shared baselines.",
                "docs/detection-types.md#structuredconfigxml"),
        ],
        References: ["docs/detection-types.md#structuredconfigxml"]);

    private static readonly FormatClass XmlGeneric = new FormatClass(
        Name: "XmlGeneric",
        Slug: "xml",
        Priority: 30,
        DefaultSeverity: "medium",
        DefaultVariant: "generic",
        Aliases: ["xml-generic"],
        Subtypes:
        [
            new FormatSubtype("MsbuildTargetsXml", 31, Variant: "msbuild-targets", Severity: "medium"),
            new FormatSubtype("MsbuildPropsXml", 32, Variant: "msbuild-props", Severity: "medium"),
            new FormatSubtype("MsbuildProjectXml", 33, Variant: "msbuild-project", Severity: "medium"),
            new FormatSubtype("WindowsManifestXml", 34, Variant: "app-manifest-xml", Severity: "medium"),
            new FormatSubtype("ResxXml", 35, Variant: "resource-xml", Severity: "medium"),
            new FormatSubtype("XamlUiXml", 36, Variant: "interface-xml", Severity: "medium"),
            new FormatSubtype("XsltStylesheetXml", 37, Variant: "xslt-xml", Severity: "medium"),
        ],
        SeverityHint:
            "Generic XML manifests advertise capabilities, endpoints, and policy grants that can expose "
            + "infrastructure layout when leaked.",
        RemediationHints:
        [
            new RemediationHint(
                "xml-provenance-review",
                "review",
                "Confirm manifest namespaces and deployment identifiers map to approved environments "
                + "before sharing samples externally.",
                "docs/detection-types.md#xml"),
            new RemediationHint(
                "xml-sanitise-identifiers",
                "sanitisation",
                "Strip unique identifiers or replace them with anonymised tokens prior to archiving manifests "
                + "in shared stores.",
                "docs/detection-types.md#xml"),
        ],
        References: ["docs/detection-types.md#xml"]);

    private static readonly FormatClass Json = new FormatClass(
        Name: "Json",
        Slug: "json",
        Priority: 40,
        DefaultSeverity: "medium",
        DefaultVariant: "generic",
        Subtypes:
        [
            new FormatSubtype("JsonWithComments", 41, Variant: "jsonc"),
            new FormatSubtype("StructuredSettingsJson", 42, Variant: "structured-settings-json"),
        ],
        SeverityHint: "JSON configuration files reveal feature flags, API endpoints, and secrets that map directly to runtime access.",
        RemediationHints:
        [
            new RemediationHint(
                "json-secret-rotation",
                "secrets",
                "Rotate keys or tokens stored in captured JSON configs and ensure redacted copies replace archival snapshots.",
                "docs/detection-types.md#json"),
            new RemediationHint(
                "json-flag-review",
                "review",
                "Audit feature toggles and environment overrides before applying configs to ensure they respect "
                + "approved deployment policies.",
                "docs/detection-types.md#json"),
        ],
        References: ["docs/detection-types.md#json"]);

    private static readonly FormatClass Yaml = new FormatClass(
        Name: "Yaml",
        Slug: "yaml",
        Priority: 50,
        DefaultSeverity: "medium",
        DefaultVariant: "generic",
        Subtypes: [new FormatSubtype("KubernetesManifest", 51, Variant: "kubernetes-manifest", Severity: "medium")],
        SeverityHint:
            "YAML manifests encode infrastructure state, secrets references, and rollout policies that leak "
            + "environment topology.",
        RemediationHints:
        [
            new RemediationHint(
                "yaml-secret-reference-audit",
                "review",
                "Audit Secret and ConfigMap references before distributing manifests and scrub environment "
                + "identifiers when possible.",
                "docs/detection-types.md#yaml"),
            new RemediationHint(
                "yaml-deployment-scope",
                "hardening",
                "Verify namespace and replica settings to prevent accidental cross-environment rollouts "
                + "when replaying manifests.",
                "docs/detection-types.md#yaml"),
        ],
        References: ["docs/detection-types.md#yaml"]);

    private static readonly FormatClass Toml = new FormatClass(
        Name: "Toml",
        Slug: "toml",
        Priority: 60,
        DefaultSeverity: "medium",
        DefaultVariant: "generic",
        Subtypes:
        [
            new FormatSubtype("ArrayOfTablesToml", 61, Variant: "array-of-tables", Severity: "medium"),
            new FormatSubtype("PackageManifestToml", 62, Variant: "package-manifest-toml", Severity: "medium"),
            new FormatSubtype("ProjectSettingsToml", 63, Variant: "project-settings-toml", Severity: "medium"),
        ],
        SeverityHint:
            "TOML project manifests reveal dependency feeds, signing requirements, and build output paths "
            + "that identify release pipelines.",
        RemediationHints:
        [
            new RemediationHint(
                "toml-feed-audit",
                "review",
                "Review [[tool]] sections for internal registries or credentials and relocate them to secure "
                + "secret stores before sharing manifests.",
                "docs/detection-types.md#toml"),
            new RemediationHint(
                "toml-build-scope",
                "hardening",
                "Sanitise path and signing configuration to avoid leaking build infrastructure details in exported manifests.",
                "docs/detection-types.md#toml"),
        ],
        References: ["docs/detection-types.md#toml"]);

    private static readonly FormatClass Ini = new FormatClass(
        Name: "Ini",
        Slug: "ini",
        Priority: 70,
        DefaultSeverity: "medium",
        DefaultVariant: "sectioned-ini",
        Aliases: ["env-file", "ini-json-hybrid"],
        Subtypes:
        [
            new FormatSubtype("SectionedIni", 71, Variant: "sectioned-ini", Severity: "medium"),
            new FormatSubtype("SectionlessIni", 72, Variant: "sectionless-ini", Severity: "medium"),
            new FormatSubtype("DesktopIni", 73, Variant: "desktop-ini", Severity: "medium"),
            new FormatSubtype("IniJsonHybrid", 74, Variant: "section-json-hybrid", Severity: "medium"),
            new FormatSubtype(
                "Dotenv",
                75,
                Variant: "dotenv",
                Severity: "high",
                Aliases: ["env", "env-file"],
                SeverityHint:
                    "Dotenv environment files commonly store plaintext secrets, service URLs, "
                    + "and deployment toggles that require immediate rotation when discovered.",
                RemediationHints:
                [
                    new RemediationHint(
                        "ini-dotenv-rotate-secrets",
                        "secrets",
                        "Rotate keys stored in dotenv files and replace evidence copies with sanitised variants "
                        + "before sharing.",
                        "docs/detection-types.md#ini-dotenv"),
                    new RemediationHint(
                        "ini-dotenv-sanitise-identifiers",
                        "sanitisation",
                        "Strip hostnames and environment identifiers from dotenv exports prior to archiving or distribution.",
                        "docs/detection-types.md#ini-dotenv"),
                ]),
            new FormatSubtype("JavaPropertiesIni", 76, Variant: "java-properties", Severity: "medium"),
        ],
        SeverityHint:
            "INI and dotenv style files often embed credentials, tokens, and environment toggles "
            + "that impact access control immediately.",
        RemediationHints:
        [
            new RemediationHint(
                "ini-secret-rotation",
                "secrets",
                "Rotate secrets surfaced in dotenv or credential sections and confirm masked samples replace raw exports.",
                "docs/detection-types.md#ini"),
            new RemediationHint(
                "ini-sanitisation-workflow",
                "sanitisation",
                "Follow the sanitisation workflow before sharing dotenv fixtures to prevent leaking production values.",
                "docs/detection-types.md#ini"),
        ],
        References: ["docs/detection-types.md#ini"]);

    private static readonly FormatClass Hcl = new FormatClass(
        Name: "Hcl",
        Slug: "hcl",
        Priority: 75,
        DefaultSeverity: "high",
        DefaultVariant: "generic",
        Subtypes:
        [
            new FormatSubtype("NomadJobHcl", 76, Variant: "hashicorp-nomad", Severity: "high"),
            new FormatSubtype("VaultServerHcl", 77, Variant: "hashicorp-vault", Severity: "high"),
            new FormatSubtype("ConsulAgentHcl", 78, Variant: "hashicorp-consul", Severity: "high"),
            new FormatSubtype("GenericHcl", 79, Variant: "generic", Severity: "high"),
        ],
        SeverityHint:
            "HCL configurations describe Nomad jobs, Vault listeners and seals, and Consul agents, "
            + "and routinely embed tokens, TLS material paths, and cluster addresses.",
        RemediationHints:
        [
            new RemediationHint(
                "hcl-token-rotation",
                "secrets",
                "Rotate tokens and unseal material referenced in captured HCL and replace archived copies with redacted variants."),
            new RemediationHint(
                "hcl-listener-review",
                "hardening",
                "Review listener, TLS, and ACL stanzas against hardened baselines before redeploying captured configs."),
        ]);

    private static readonly FormatClass KeyValueProperties = new FormatClass(
        Name: "KeyValueProperties",
        Slug: "properties",
        Priority: 80,
        DefaultSeverity: "medium",
        DefaultVariant: "java-properties",
        SeverityHint:
            "Java-style properties files concentrate service endpoints, credentials, and feature toggles "
            + "for entire JVM applications.",
        RemediationHints:
        [
            new RemediationHint(
                "properties-credential-scan",
                "secrets",
                "Scan captured properties for passwords or tokens and migrate them into managed secret stores immediately.",
                "docs/detection-types.md#keyvalueproperties"),
            new RemediationHint(
                "properties-comment-scrub",
                "sanitisation",
                "Review inline comments for deployment notes or hostnames and redact sensitive context before sharing.",
                "docs/detection-types.md#keyvalueproperties"),
        ],
        References: ["docs/detection-types.md#keyvalueproperties"]);

    private static readonly FormatClass UnixConf = new FormatClass(
        Name: "UnixConf",
        Slug: "unix-conf",
        Priority: 90,
        DefaultSeverity: "high",
        DefaultVariant: "directive-conf",
        Subtypes:
        [
            new FormatSubtype("DirectiveConf", 91, Variant: "directive-conf", Severity: "high"),
            new FormatSubtype("ApacheConf", 92, Variant: "apache-conf", Severity: "high"),
            new FormatSubtype("NginxConf", 93, Variant: "nginx-conf", Severity: "high"),
            new FormatSubtype("GenericDirectiveText", 94, Variant: "generic-directive-text", Severity: "medium"),
            new FormatSubtype("OpensshConf", 95, Variant: "openssh-conf", Severity: "high"),
            new FormatSubtype("OpenvpnConf", 96, Variant: "openvpn-conf", Severity: "high"),
            new FormatSubtype("LogstashPipelineConf", 97, Variant: "logstash-pipeline", Severity: "high"),
        ],
        SeverityHint:
            "Unix configuration files govern listeners, crypto policies, and authentication hooks "
            + "that immediately influence service exposure.",
        RemediationHints:
        [
            new RemediationHint(
                "unix-conf-hardening",
                "hardening",
                "Review captured directives against hardened baselines and disable permissive modules "
                + "before redeploying configs.",
                "docs/detection-types.md#unixconf"),
            new RemediationHint(
                "unix-conf-access-review",
                "review",
                "Confirm referenced key, certificate, and log paths carry restricted permissions before sharing archives.",
                "docs/detection-types.md#unixconf"),
        ],
        References: ["docs/detection-types.md#unixconf"]);

    private static readonly FormatClass ScriptConfig = new FormatClass(
        Name: "ScriptConfig",
        Slug: "script-config",
        Priority: 100,
        DefaultSeverity: "high",
        DefaultVariant: "generic",
        Aliases: ["dockerfile"],
        Subtypes:
        [
            new FormatSubtype("PowerShellConfig", 101, Variant: "ps1-shell", Severity: "high"),
            new FormatSubtype("BatchScriptConfig", 102, Variant: "batch-script", Severity: "high"),
            new FormatSubtype("CmdScriptConfig", 103, Variant: "cmd-shell", Severity: "high"),
            new FormatSubtype("VbscriptConfig", 104, Variant: "vbscript", Severity: "high"),
            new FormatSubtype("ContainerBuildScript", 105, Variant: "generic", Severity: "high", Aliases: ["dockerfile"]),
        ],
        SeverityHint:
            "Script-based configs can execute arbitrary changes, embed credentials, and provision infrastructure "
            + "when replayed without review.",
        RemediationHints:
        [
            new RemediationHint(
                "script-config-scope",
                "review",
                "Validate script scopes and ensure they run against lab environments before applying to production hosts.",
                "docs/detection-types.md#scriptconfig"),
            new RemediationHint(
                "script-config-secret-hygiene",
                "secrets",
                "Replace inline credentials with secure parameter stores and scrub tokens before archiving scripts.",
                "docs/detection-types.md#scriptconfig"),
        ],
        References: ["docs/detection-types.md#scriptconfig"]);

    private static readonly FormatClass EmbeddedSqlDb = new FormatClass(
        Name: "EmbeddedSqlDb",
        Slug: "embedded-sql-db",
        Priority: 110,
        DefaultSeverity: "high",
        DefaultVariant: "generic",
        Aliases: ["embedded-sql", "embedded-sqlite", "sqlite"],
        SeverityHint:
            "Embedded SQLite databases retain raw operational data, including user records and tokens, "
            + "making them high-risk evidence.",
        RemediationHints:
        [
            new RemediationHint(
                "embedded-sql-redaction",
                "sanitisation",
                "Mask or drop sensitive rows before distributing captured databases and document transformations "
                + "in the evidence log.",
                "docs/detection-types.md#embeddedsqldb"),
            new RemediationHint(
                "embedded-sql-retention",
                "retention",
                "Apply the 30-day retention policy and record purge decisions once investigations close.",
                "docs/detection-types.md#embeddedsqldb"),
        ],
        References: ["docs/detection-types.md#embeddedsqldb"]);

    private static readonly FormatClass GenericBinaryDat = new FormatClass(
        Name: "GenericBinaryDat",
        Slug: "binary-dat",
        Priority: 120,
        DefaultSeverity: "low",
        DefaultVariant: "generic",
        Aliases: ["binary"],
        SeverityHint: "Opaque binary blobs are unclassified evidence; treat them cautiously until confirmed non-sensitive.",
        RemediationHints:
        [
            new RemediationHint(
                "binary-dat-triage",
                "review",
                "Triages samples with dedicated tooling before storing them long term to determine whether "
                + "further sanitisation is required.",
                "docs/detection-types.md#genericbinarydat"),
            new RemediationHint(
                "binary-dat-redaction",
                "sanitisation",
                "If the blob contains extracted credentials or certificates, replace it with hashed summaries before sharing.",
                "docs/detection-types.md#genericbinarydat"),
        ],
        References: ["docs/detection-types.md#genericbinarydat"]);

    private static readonly FormatClass Plist = new FormatClass(
        Name: "Plist",
        Slug: "plist",
        Priority: 130,
        DefaultSeverity: "medium",
        DefaultVariant: "xml-or-binary",
        SeverityHint:
            "Property lists carry application preferences, launch agent definitions, and embedded credentials "
            + "for desktop and mobile tooling.",
        RemediationHints:
        [
            new RemediationHint(
                "plist-secret-review",
                "secrets",
                "Review captured property lists for stored tokens or account identifiers and redact them before sharing."),
        ]);

    private static readonly FormatClass MarkdownConfig = new FormatClass(
        Name: "MarkdownConfig",
        Slug: "markdown-config",
        Priority: 140,
        DefaultSeverity: "low",
        DefaultVariant: "embedded-yaml-frontmatter",
        SeverityHint:
            "Markdown front matter carries site generator settings and publishing metadata that can expose "
            + "author identities and internal paths.",
        RemediationHints:
        [
            new RemediationHint(
                "markdown-frontmatter-scrub",
                "sanitisation",
                "Strip author, host, and path identifiers from front matter before archiving captured documents."),
        ]);

    private static readonly FallbackClass Fallback = new FallbackClass("UnknownTextOrBinary", "unknown-text-or-binary", 1000, "info");

    internal static DetectionCatalog Build() => new(
        Version: "0.0.3",
        Updated: "2025-10-10",
        Classes:
        [
            RegistryExport,
            RegistryLive,
            StructuredConfigXml,
            XmlGeneric,
            Json,
            Yaml,
            Toml,
            Ini,
            Hcl,
            KeyValueProperties,
            UnixConf,
            ScriptConfig,
            EmbeddedSqlDb,
            GenericBinaryDat,
            Plist,
            MarkdownConfig,
        ],
        Fallback: Fallback);
}
