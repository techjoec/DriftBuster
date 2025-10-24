"""Canonical detection catalog for DriftBuster.

The JSON scaffolds previously used during prototyping have been baked into a
typed Python module so the rest of the project can import structured metadata
without juggling file I/O. Two data sets are represented:

* ``DETECTION_CATALOG`` — the priority-ordered detection class definitions used
  by the core detector (formerly ``TYPES.json`` v0.0.2).
* ``FORMAT_SURVEY`` — usage-oriented estimates for future planning (the format
  survey v0.0.2 provided by the user).

Nothing consumes these structures directly yet, but storing them in code keeps
the source of truth close to the implementation and makes refactors easier down
the line.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Tuple


@dataclass(frozen=True)
class ContentSignature:
    """Signature hint used by detection heuristics."""

    type: str
    pattern: str | None = None
    optional: bool = False
    multiline: bool = False
    max_bytes: int | None = None
    offset: int | None = None
    hex: str | None = None
    non_text_ratio_gt: float | None = None
    sample_bytes: int | None = None


@dataclass(frozen=True)
class FormatSubtype:
    """Subtype definition for a detection class."""

    name: str
    priority: int
    variant: str | None = None
    severity: str | None = None
    filename_patterns: Tuple[str, ...] = field(default_factory=tuple)
    content_signatures: Tuple[ContentSignature, ...] = field(default_factory=tuple)
    mime_hints: Tuple[str, ...] = field(default_factory=tuple)
    aliases: Tuple[str, ...] = field(default_factory=tuple)


@dataclass(frozen=True)
class RemediationHint:
    """Recommended follow-up action for a detection class."""

    id: str
    category: str
    summary: str
    documentation: str | None = None


@dataclass(frozen=True)
class FormatClass:
    """Primary detection class definition."""

    name: str
    slug: str
    priority: int
    default_severity: str
    extensions: Tuple[str, ...]
    default_variant: str | None = None
    aliases: Tuple[str, ...] = field(default_factory=tuple)
    filename_patterns: Tuple[str, ...] = field(default_factory=tuple)
    content_signatures: Tuple[ContentSignature, ...] = field(default_factory=tuple)
    mime_hints: Tuple[str, ...] = field(default_factory=tuple)
    examples: Tuple[str, ...] = field(default_factory=tuple)
    subtypes: Tuple[FormatSubtype, ...] = field(default_factory=tuple)
    severity_hint: str | None = None
    remediation_hints: Tuple[RemediationHint, ...] = field(default_factory=tuple)


@dataclass(frozen=True)
class FallbackClass:
    name: str
    slug: str
    priority: int
    default_severity: str
    mime_hints: Tuple[str, ...]
    aliases: Tuple[str, ...] = field(default_factory=tuple)


@dataclass(frozen=True)
class DetectionCatalog:
    version: str
    updated: str
    notes: Tuple[str, ...]
    classes: Tuple[FormatClass, ...]
    fallback: FallbackClass


DETECTION_CATALOG = DetectionCatalog(
    version="0.0.2",
    updated="2025-10-10",
    notes=(
        "Detection runs in priority order; the first positive match wins.",
        "Heuristics combine filename/extension and lightweight content-signature checks.",
        "Correction: .resx is XML-based resources, not binary.",
    ),
    classes=(
        FormatClass(
            name="RegistryExport",
            slug="registry-export",
            priority=10,
            default_severity="high",
            extensions=(".reg",),
            filename_patterns=("(?i)^.*\.reg$",),
            content_signatures=(
                ContentSignature(
                    type="starts_with_regex",
                    pattern="^(Windows Registry Editor Version (4|5)\.00|REGEDIT4)\r?\n",
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^\[HKEY_(LOCAL_MACHINE|CURRENT_USER|CLASSES_ROOT|USERS|CURRENT_CONFIG)\\.+\]$",
                ),
            ),
            mime_hints=("application/regedit", "text/plain"),
            examples=(
                'Windows Registry Editor Version 5.00\n[HKEY_CURRENT_USER\\Software\\Vendor\\App]\n"Key"="Value"',
            ),
            severity_hint="Registry exports capture entire hive snapshots, including secrets, policy settings, and service fingerprints.",
            remediation_hints=(
                RemediationHint(
                    id="registry-export-lockdown",
                    category="secrets",
                    summary="Store exported hives in restricted evidence shares and rotate credentials referenced in the dump.",
                    documentation="docs/detection-types.md#registryexport",
                ),
            ),
        ),
        FormatClass(
            name="RegistryLive",
            slug="registry-live",
            priority=15,
            default_severity="medium",
            extensions=(".json", ".yml", ".yaml"),
            default_variant="scan-definition",
            filename_patterns=(
                "(?i)^.*\.(regscan\.json|registry\.json)$",
                "(?i)^(registry|reg).*\.(json|ya?ml)$",
            ),
            content_signatures=(
                ContentSignature(
                    type="contains_regex",
                    pattern=r'"registry_scan"\s*:\s*\{',
                    optional=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern=r"^\s*registry_scan\s*:\s*$",
                    multiline=True,
                    optional=True,
                ),
            ),
            mime_hints=("application/json", "text/yaml", "text/plain"),
            examples=(
                '{"registry_scan": {"token": "Vendor App", "keywords": ["server"]}}',
                'registry_scan:\n  token: Vendor App\n  keywords: [server]',
            ),
            severity_hint="Registry scan definitions describe automated hive reads and target tokens that expose sensitive audit scope.",
            remediation_hints=(
                RemediationHint(
                    id="registry-live-scope-review",
                    category="review",
                    summary="Confirm monitoring tokens align with approved hosts and rotate any credentials referenced in the manifest.",
                    documentation="docs/detection-types.md#registrylive",
                ),
            ),
        ),
        FormatClass(
            name="StructuredConfigXml",
            slug="structured-config-xml",
            priority=20,
            default_severity="high",
            extensions=(".config",),
            default_variant="web-or-app-config",
            aliases=("structured-config",),
            filename_patterns=("(?i)^.*\.(config)$", "(?i)^(app|web|machine)\.config$"),
            content_signatures=(
                ContentSignature(type="contains_regex", pattern="<configuration(\s|>)"),
                ContentSignature(type="contains_regex", pattern="<(appSettings|runtime|system\.web)(\s|>)"),
            ),
            mime_hints=("application/xml", "text/xml"),
            examples=(
                "<configuration xmlns:xdt=\"http://schemas.example/xdt\">...</configuration>",
            ),
            subtypes=(
                FormatSubtype(
                    name="WebConfigXml",
                    priority=21,
                    variant="web-config",
                    severity="high",
                ),
                FormatSubtype(
                    name="AppConfigXml",
                    priority=22,
                    variant="app-config",
                    severity="high",
                ),
                FormatSubtype(
                    name="MachineConfigXml",
                    priority=23,
                    variant="machine-config",
                    severity="high",
                ),
                FormatSubtype(
                    name="WebConfigTransform",
                    priority=24,
                    variant="web-config-transform",
                    severity="high",
                ),
                FormatSubtype(
                    name="AppConfigTransform",
                    priority=25,
                    variant="app-config-transform",
                    severity="high",
                ),
                FormatSubtype(
                    name="MachineConfigTransform",
                    priority=26,
                    variant="machine-config-transform",
                    severity="high",
                ),
                FormatSubtype(
                    name="GenericConfigTransform",
                    priority=27,
                    variant="config-transform",
                    severity="high",
                ),
                FormatSubtype(
                    name="CustomConfigXml",
                    priority=28,
                    variant="custom-config-xml",
                    severity="medium",
                    aliases=("sample",),
                ),
            ),
            severity_hint="Application configuration files expose secrets, connection strings, and runtime policy toggles that impact production systems.",
            remediation_hints=(
                RemediationHint(
                    id="structured-config-rotate-secrets",
                    category="secrets",
                    summary="Rotate credentials stored in configuration sections and confirm transforms match approved deployment scopes.",
                    documentation="docs/detection-types.md#structuredconfigxml",
                ),
                RemediationHint(
                    id="structured-config-hardening",
                    category="hardening",
                    summary="Review debug switches and permissive runtime settings before promoting captured configs to shared baselines.",
                    documentation="docs/detection-types.md#structuredconfigxml",
                ),
            ),
        ),
        FormatClass(
            name="XmlGeneric",
            slug="xml",
            priority=30,
            default_severity="medium",
            extensions=(".xml", ".manifest", ".resx", ".xaml"),
            default_variant="generic",
            aliases=("xml-generic",),
            filename_patterns=("(?i)^.*\.(xml|manifest|resx|xaml)$",),
            content_signatures=(
                ContentSignature(
                    type="starts_with_regex",
                    pattern=r'^\s*<\?xml\s+version\s*=\s*"[^"]+"',
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^\s*<[^!?][\w:.-]+(\s|>)",
                ),
            ),
            mime_hints=("application/xml", "text/xml"),
            examples=(
                "<assembly xmlns=\"urn:example:driftbuster:manifest\" xmlns:compat=\"urn:example:driftbuster:compatibility\">...</assembly>",
            ),
            subtypes=(
                FormatSubtype(
                    name="MsbuildTargetsXml",
                    priority=31,
                    variant="msbuild-targets",
                    severity="medium",
                ),
                FormatSubtype(
                    name="MsbuildPropsXml",
                    priority=32,
                    variant="msbuild-props",
                    severity="medium",
                ),
                FormatSubtype(
                    name="MsbuildProjectXml",
                    priority=33,
                    variant="msbuild-project",
                    severity="medium",
                ),
                FormatSubtype(
                    name="WindowsManifestXml",
                    priority=34,
                    variant="app-manifest-xml",
                    filename_patterns=("(?i)^.*\.manifest$",),
                    content_signatures=(
                        ContentSignature(type="contains_regex", pattern="<assembly(\s|>)"),
                        ContentSignature(
                            type="contains_regex",
                            pattern=r'xmlns="urn:schemas-microsoft-com:asm\.v1"',
                        ),
                    ),
                    severity="medium",
                ),
                FormatSubtype(
                    name="ResxXml",
                    priority=35,
                    variant="resource-xml",
                    filename_patterns=("(?i)^.*\.resx$",),
                    content_signatures=(
                        ContentSignature(type="contains_regex", pattern="<root(\s|>)"),
                        ContentSignature(type="contains_regex", pattern=r'<data\s+name="'),
                    ),
                    severity="medium",
                ),
                FormatSubtype(
                    name="XamlUiXml",
                    priority=36,
                    variant="interface-xml",
                    filename_patterns=("(?i)^.*\.xaml$",),
                    content_signatures=(
                        ContentSignature(
                            type="contains_regex",
                            pattern=r'xmlns(:\w+)?="http://schemas\.microsoft\.com/winfx/2006/xaml"',
                        ),
                        ContentSignature(
                            type="contains_regex",
                            pattern="<(Window|UserControl|Page|Application|ResourceDictionary)(\s|>)",
                        ),
                    ),
                    severity="medium",
                ),
                FormatSubtype(
                    name="XsltStylesheetXml",
                    priority=37,
                    variant="xslt-xml",
                    severity="medium",
                ),
            ),
            severity_hint="Generic XML manifests advertise capabilities, endpoints, and policy grants that can expose infrastructure layout when leaked.",
            remediation_hints=(
                RemediationHint(
                    id="xml-provenance-review",
                    category="review",
                    summary="Confirm manifest namespaces and deployment identifiers map to approved environments before sharing samples externally.",
                    documentation="docs/detection-types.md#xml",
                ),
                RemediationHint(
                    id="xml-sanitise-identifiers",
                    category="sanitisation",
                    summary="Strip unique identifiers or replace them with anonymised tokens prior to archiving manifests in shared stores.",
                    documentation="docs/detection-types.md#xml",
                ),
            ),
        ),
        FormatClass(
            name="Json",
            slug="json",
            priority=40,
            default_severity="medium",
            extensions=(".json", ".jsonc"),
            default_variant="generic",
            filename_patterns=("(?i)^.*\.(json|jsonc)$",),
            content_signatures=(
                ContentSignature(type="starts_with_regex", pattern="^\s*[\[{]"),
                ContentSignature(
                    type="not_contains_regex",
                    pattern="\/\/|/\*",
                    optional=True,
                ),
                ContentSignature(type="json_parse_probe", max_bytes=2_097_152),
            ),
            mime_hints=("application/json",),
            subtypes=(
                FormatSubtype(
                    name="JsonWithComments",
                    priority=41,
                    variant="jsonc",
                    filename_patterns=("(?i)^.*\.jsonc$",),
                    content_signatures=(
                        ContentSignature(
                            type="contains_regex",
                            pattern="(^|\n)\s*(\/\/|/\*)",
                        ),
                    ),
                    mime_hints=("application/json", "text/plain"),
                ),
                FormatSubtype(
                    name="StructuredSettingsJson",
                    priority=42,
                    variant="structured-settings-json",
                    filename_patterns=("(?i)^appsettings(\.[A-Za-z0-9_-]+)?\.json$",),
                    content_signatures=(
                        ContentSignature(
                            type="contains_regex",
                            pattern='"Logging"\s*:\s*\{',
                        ),
                        ContentSignature(
                            type="contains_regex",
                            pattern='"ConnectionStrings"\s*:\s*\{',
                            optional=True,
                        ),
                    ),
                ),
            ),
            severity_hint="JSON configuration files reveal feature flags, API endpoints, and secrets that map directly to runtime access.",
            remediation_hints=(
                RemediationHint(
                    id="json-secret-rotation",
                    category="secrets",
                    summary="Rotate keys or tokens stored in captured JSON configs and ensure redacted copies replace archival snapshots.",
                    documentation="docs/detection-types.md#json",
                ),
                RemediationHint(
                    id="json-flag-review",
                    category="review",
                    summary="Audit feature toggles and environment overrides before applying configs to ensure they respect approved deployment policies.",
                    documentation="docs/detection-types.md#json",
                ),
            ),
        ),
        FormatClass(
            name="Yaml",
            slug="yaml",
            priority=50,
            default_severity="medium",
            extensions=(".yml", ".yaml"),
            default_variant="generic",
            filename_patterns=("(?i)^.*\.(ya?ml)$",),
            content_signatures=(
                ContentSignature(
                    type="starts_with_regex",
                    pattern="^\s*---\s*$",
                    optional=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^[ \\t]*[A-Za-z0-9_\\-\"']+[ \\t]*:(\\s|$)",
                    multiline=True,
                ),
            ),
            mime_hints=("application/yaml", "text/yaml", "text/plain"),
            subtypes=(
                FormatSubtype(
                    name="KubernetesManifest",
                    priority=51,
                    variant="kubernetes-manifest",
                    severity="medium",
                ),
            ),
            severity_hint="YAML manifests encode infrastructure state, secrets references, and rollout policies that leak environment topology.",
            remediation_hints=(
                RemediationHint(
                    id="yaml-secret-reference-audit",
                    category="review",
                    summary="Audit Secret and ConfigMap references before distributing manifests and scrub environment identifiers when possible.",
                    documentation="docs/detection-types.md#yaml",
                ),
                RemediationHint(
                    id="yaml-deployment-scope",
                    category="hardening",
                    summary="Verify namespace and replica settings to prevent accidental cross-environment rollouts when replaying manifests.",
                    documentation="docs/detection-types.md#yaml",
                ),
            ),
        ),
        FormatClass(
            name="Toml",
            slug="toml",
            priority=60,
            default_severity="medium",
            extensions=(".toml",),
            default_variant="generic",
            filename_patterns=("(?i)^.*\.toml$",),
            content_signatures=(
                ContentSignature(
                    type="contains_regex",
                    pattern="^\s*\[[A-Za-z0-9_.\-]+\]\s*$",
                    multiline=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^[A-Za-z0-9_\-]+\s*=\s*[^\n]+$",
                    multiline=True,
                ),
            ),
            mime_hints=("application/toml", "text/plain"),
            subtypes=(
                FormatSubtype(
                    name="ArrayOfTablesToml",
                    priority=61,
                    variant="array-of-tables",
                    severity="medium",
                ),
                FormatSubtype(
                    name="PackageManifestToml",
                    priority=62,
                    variant="package-manifest-toml",
                    filename_patterns=("^Cargo\.toml$",),
                    severity="medium",
                ),
                FormatSubtype(
                    name="ProjectSettingsToml",
                    priority=63,
                    variant="project-settings-toml",
                    filename_patterns=("^pyproject\.toml$",),
                    severity="medium",
                ),
            ),
            severity_hint="TOML project manifests reveal dependency feeds, signing requirements, and build output paths that identify release pipelines.",
            remediation_hints=(
                RemediationHint(
                    id="toml-feed-audit",
                    category="review",
                    summary="Review [[tool]] sections for internal registries or credentials and relocate them to secure secret stores before sharing manifests.",
                    documentation="docs/detection-types.md#toml",
                ),
                RemediationHint(
                    id="toml-build-scope",
                    category="hardening",
                    summary="Sanitise path and signing configuration to avoid leaking build infrastructure details in exported manifests.",
                    documentation="docs/detection-types.md#toml",
                ),
            ),
        ),
        FormatClass(
            name="Ini",
            slug="ini",
            priority=70,
            default_severity="medium",
            extensions=(".ini", ".cfg", ".cnf"),
            default_variant="sectioned-ini",
            aliases=("env-file", "ini-json-hybrid", "hcl"),
            filename_patterns=("(?i)^.*\.(ini|cfg|cnf)$", "(?i)^desktop\.ini$"),
            content_signatures=(
                ContentSignature(
                    type="contains_regex",
                    pattern="^\s*\[[^\]\n]+\]\s*$",
                    multiline=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^[A-Za-z0-9_.\-]+\s*=\s*[^\n]*$",
                    multiline=True,
                ),
            ),
            mime_hints=("text/plain",),
            subtypes=(
                FormatSubtype(
                    name="SectionedIni",
                    priority=71,
                    variant="sectioned-ini",
                    severity="medium",
                ),
                FormatSubtype(
                    name="SectionlessIni",
                    priority=72,
                    variant="sectionless-ini",
                    severity="medium",
                ),
                FormatSubtype(
                    name="DesktopIni",
                    priority=73,
                    variant="desktop-ini",
                    severity="medium",
                    filename_patterns=("(?i)^desktop\.ini$",),
                ),
                FormatSubtype(
                    name="IniJsonHybrid",
                    priority=74,
                    variant="section-json-hybrid",
                    severity="medium",
                ),
                FormatSubtype(
                    name="Dotenv",
                    priority=75,
                    variant="dotenv",
                    severity="medium",
                ),
                FormatSubtype(
                    name="JavaPropertiesIni",
                    priority=76,
                    variant="java-properties",
                    severity="medium",
                ),
            ),
            severity_hint="INI and dotenv style files often embed credentials, tokens, and environment toggles that impact access control immediately.",
            remediation_hints=(
                RemediationHint(
                    id="ini-secret-rotation",
                    category="secrets",
                    summary="Rotate secrets surfaced in dotenv or credential sections and confirm masked samples replace raw exports.",
                    documentation="docs/detection-types.md#ini",
                ),
                RemediationHint(
                    id="ini-sanitisation-workflow",
                    category="sanitisation",
                    summary="Follow the sanitisation workflow before sharing dotenv fixtures to prevent leaking production values.",
                    documentation="docs/detection-types.md#ini",
                ),
            ),
        ),
        FormatClass(
            name="KeyValueProperties",
            slug="properties",
            priority=80,
            default_severity="medium",
            extensions=(".properties",),
            default_variant="java-properties",
            filename_patterns=("(?i)^.*\.properties$",),
            content_signatures=(
                ContentSignature(
                    type="contains_regex",
                    pattern="^[#!].*$",
                    multiline=True,
                    optional=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^[A-Za-z0-9_.\-]+\s*(=|:)\s*.*$",
                    multiline=True,
                ),
            ),
            mime_hints=("text/plain",),
            severity_hint="Java-style properties files concentrate service endpoints, credentials, and feature toggles for entire JVM applications.",
            remediation_hints=(
                RemediationHint(
                    id="properties-credential-scan",
                    category="secrets",
                    summary="Scan captured properties for passwords or tokens and migrate them into managed secret stores immediately.",
                    documentation="docs/detection-types.md#keyvalueproperties",
                ),
                RemediationHint(
                    id="properties-comment-scrub",
                    category="sanitisation",
                    summary="Review inline comments for deployment notes or hostnames and redact sensitive context before sharing.",
                    documentation="docs/detection-types.md#keyvalueproperties",
                ),
            ),
        ),
        FormatClass(
            name="UnixConf",
            slug="unix-conf",
            priority=90,
            default_severity="high",
            extensions=(".conf",),
            default_variant="directive-conf",
            filename_patterns=("(?i)^.*\.conf$",),
            content_signatures=(
                ContentSignature(
                    type="contains_regex",
                    pattern="^(\s*#|\s*;|\s*[A-Za-z0-9_.\-]+\s+[^\n]+)$",
                    multiline=True,
                ),
            ),
            mime_hints=("text/plain",),
            subtypes=(
                FormatSubtype(
                    name="DirectiveConf",
                    priority=91,
                    variant="directive-conf",
                    severity="high",
                ),
                FormatSubtype(
                    name="ApacheConf",
                    priority=92,
                    variant="apache-conf",
                    severity="high",
                ),
                FormatSubtype(
                    name="NginxConf",
                    priority=93,
                    variant="nginx-conf",
                    severity="high",
                ),
                FormatSubtype(
                    name="GenericDirectiveText",
                    priority=94,
                    variant="generic-directive-text",
                    severity="medium",
                ),
                FormatSubtype(
                    name="OpensshConf",
                    priority=95,
                    variant="openssh-conf",
                    severity="high",
                ),
                FormatSubtype(
                    name="OpenvpnConf",
                    priority=96,
                    variant="openvpn-conf",
                    severity="high",
                ),
            ),
            severity_hint="Unix configuration files govern listeners, crypto policies, and authentication hooks that immediately influence service exposure.",
            remediation_hints=(
                RemediationHint(
                    id="unix-conf-hardening",
                    category="hardening",
                    summary="Review captured directives against hardened baselines and disable permissive modules before redeploying configs.",
                    documentation="docs/detection-types.md#unixconf",
                ),
                RemediationHint(
                    id="unix-conf-access-review",
                    category="review",
                    summary="Confirm referenced key, certificate, and log paths carry restricted permissions before sharing archives.",
                    documentation="docs/detection-types.md#unixconf",
                ),
            ),
        ),
        FormatClass(
            name="ScriptConfig",
            slug="script-config",
            priority=100,
            default_severity="high",
            extensions=(".ps1", ".bat", ".cmd", ".vbs"),
            default_variant="generic",
            aliases=("dockerfile",),
            filename_patterns=("(?i)^.*\.(ps1|bat|cmd|vbs)$",),
            content_signatures=(
                ContentSignature(
                    type="starts_with_regex",
                    pattern=r"^#requires|^Param\(",
                    optional=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^(?:@?echo\s+off|set\s+\w+=)",
                    multiline=True,
                    optional=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern=r'CreateObject\("Scripting\.Dictionary"\)',
                    optional=True,
                ),
            ),
            mime_hints=("text/x-powershell", "text/x-batch", "text/vbscript", "text/plain"),
            subtypes=(
                FormatSubtype(
                    name="PowerShellConfig",
                    priority=101,
                    variant="ps1-shell",
                    severity="high",
                ),
                FormatSubtype(
                    name="BatchScriptConfig",
                    priority=102,
                    variant="batch-script",
                    severity="high",
                ),
                FormatSubtype(
                    name="CmdScriptConfig",
                    priority=103,
                    variant="cmd-shell",
                    severity="high",
                ),
                FormatSubtype(
                    name="VbscriptConfig",
                    priority=104,
                    variant="vbscript",
                    severity="high",
                ),
                FormatSubtype(
                    name="ContainerBuildScript",
                    priority=105,
                    variant="generic",
                    aliases=("dockerfile",),
                    severity="high",
                ),
            ),
            severity_hint="Script-based configs can execute arbitrary changes, embed credentials, and provision infrastructure when replayed without review.",
            remediation_hints=(
                RemediationHint(
                    id="script-config-scope",
                    category="review",
                    summary="Validate script scopes and ensure they run against lab environments before applying to production hosts.",
                    documentation="docs/detection-types.md#scriptconfig",
                ),
                RemediationHint(
                    id="script-config-secret-hygiene",
                    category="secrets",
                    summary="Replace inline credentials with secure parameter stores and scrub tokens before archiving scripts.",
                    documentation="docs/detection-types.md#scriptconfig",
                ),
            ),
        ),
        FormatClass(
            name="EmbeddedSqlDb",
            slug="embedded-sql-db",
            priority=110,
            default_severity="high",
            extensions=(".sqlite", ".db"),
            default_variant="generic",
            aliases=("embedded-sql", "embedded-sqlite", "sqlite"),
            filename_patterns=("(?i)^.*\.(sqlite|db)$",),
            content_signatures=(
                ContentSignature(
                    type="binary_magic",
                    offset=0,
                    hex="53514C69746520666F726D6174203300",
                ),
            ),
            mime_hints=("application/vnd.sqlite3", "application/octet-stream"),
            severity_hint="Embedded SQLite databases retain raw operational data, including user records and tokens, making them high-risk evidence.",
            remediation_hints=(
                RemediationHint(
                    id="embedded-sql-redaction",
                    category="sanitisation",
                    summary="Mask or drop sensitive rows before distributing captured databases and document transformations in the evidence log.",
                    documentation="docs/detection-types.md#embeddedsqldb",
                ),
                RemediationHint(
                    id="embedded-sql-retention",
                    category="retention",
                    summary="Apply the 30-day retention policy and record purge decisions once investigations close.",
                    documentation="docs/detection-types.md#embeddedsqldb",
                ),
            ),
        ),
        FormatClass(
            name="GenericBinaryDat",
            slug="binary-dat",
            priority=120,
            default_severity="low",
            extensions=(".dat", ".bin"),
            default_variant="generic",
            aliases=("binary",),
            filename_patterns=("(?i)^.*\.(dat|bin)$",),
            content_signatures=(
                ContentSignature(
                    type="binary_threshold",
                    non_text_ratio_gt=0.25,
                    sample_bytes=16_384,
                ),
            ),
            mime_hints=("application/octet-stream",),
            severity_hint="Opaque binary blobs are unclassified evidence; treat them cautiously until confirmed non-sensitive.",
            remediation_hints=(
                RemediationHint(
                    id="binary-dat-triage",
                    category="review",
                    summary="Triages samples with dedicated tooling before storing them long term to determine whether further sanitisation is required.",
                    documentation="docs/detection-types.md#genericbinarydat",
                ),
                RemediationHint(
                    id="binary-dat-redaction",
                    category="sanitisation",
                    summary="If the blob contains extracted credentials or certificates, replace it with hashed summaries before sharing.",
                    documentation="docs/detection-types.md#genericbinarydat",
                ),
            ),
        ),
    ),
    fallback=FallbackClass(
        name="UnknownTextOrBinary",
        slug="unknown-text-or-binary",
        priority=1000,
        default_severity="info",
        mime_hints=("text/plain", "application/octet-stream"),
    ),
)


@dataclass(frozen=True)
class UsageVariant:
    variant: str
    extensions: Tuple[str, ...] = field(default_factory=tuple)
    filename_hint: str | None = None
    mime_hints: Tuple[str, ...] = field(default_factory=tuple)
    context: str | None = None


@dataclass(frozen=True)
class FormatUsage:
    format: str
    variant: str | None
    extensions: Tuple[str, ...]
    mime_hints: Tuple[str, ...] = field(default_factory=tuple)
    context: str | None = None
    approx_usage_percent: float | None = None
    confidence_model: str | None = None
    variants: Tuple[UsageVariant, ...] = field(default_factory=tuple)


@dataclass(frozen=True)
class SurveyMeta:
    primary_key: str
    secondary_key: str
    total_formats: int
    usage_sum_percent: float
    notes: Tuple[str, ...]


@dataclass(frozen=True)
class FormatSurvey:
    version: str
    updated: str
    formats: Tuple[FormatUsage, ...]
    meta: SurveyMeta


FORMAT_SURVEY = FormatSurvey(
    version="0.0.2",
    updated="2025-10-10",
    formats=(
        FormatUsage(
            format="registry-export",
            variant=None,
            extensions=(".reg",),
            mime_hints=("application/regedit", "text/plain"),
            context="Windows Registry export/import text files; human-readable representation of system registry keys and values.",
            approx_usage_percent=10,
            confidence_model="signature+prefix",
        ),
        FormatUsage(
            format="structured-config-xml",
            variant="web-or-app-config",
            extensions=(".config",),
            mime_hints=("application/xml",),
            context="Enterprise XML configuration files (e.g., app.config, web.config, machine.config) used by general web frameworks; detector logs namespace provenance hashes with line numbers for audit trails.",
            approx_usage_percent=12,
            confidence_model="schema+namespace",
        ),
        FormatUsage(
            format="xml",
            variant="generic",
            extensions=(".xml", ".manifest", ".resx", ".xaml"),
            mime_hints=("application/xml", "text/xml"),
            context="Generic or declarative XML configuration; also includes system manifests, .resx resources, and XAML-style UI files with line-level namespace provenance logging.",
            approx_usage_percent=14,
            confidence_model="doctype+element-scan",
            variants=(
                UsageVariant(
                    variant="app-manifest-xml",
                    filename_hint=".manifest",
                    mime_hints=("application/xml",),
                    context="Application or assembly manifests declaring privileges, dependencies, or execution levels.",
                ),
                UsageVariant(
                    variant="resource-xml",
                    filename_hint=".resx",
                    mime_hints=("application/xml",),
                    context="Resource dictionaries storing localized strings and binary data for desktop applications.",
                ),
                UsageVariant(
                    variant="interface-xml",
                    filename_hint=".xaml",
                    mime_hints=("application/xml",),
                    context="Declarative UI markup for desktop interface frameworks that layer on XML.",
                ),
            ),
        ),
        FormatUsage(
            format="json",
            variant="generic",
            extensions=(".json", ".jsonc"),
            mime_hints=("application/json",),
            context="Modern configuration format for cross-platform apps, desktop shells, and service tooling.",
            approx_usage_percent=22,
            confidence_model="bracket-balance+json-parse",
            variants=(
                UsageVariant(
                    variant="jsonc",
                    extensions=(".jsonc",),
                    mime_hints=("application/json", "text/plain"),
                    context="JSON with comments; popular across developer tooling.",
                ),
                UsageVariant(
                    variant="structured-settings-json",
                    filename_hint="appsettings.json",
                    context="Primary configuration file for appsettings-style JSON layouts in modern web frameworks.",
                ),
                UsageVariant(
                    variant="runtime-package-json",
                    filename_hint="package.json",
                    context="Configuration and metadata for desktop apps built on Node-compatible runtimes.",
                ),
            ),
        ),
        FormatUsage(
            format="yaml",
            variant=None,
            extensions=(".yml", ".yaml"),
            mime_hints=("application/yaml", "text/yaml"),
            context="Human-readable configuration for DevOps, CI/CD, container orchestration, and service descriptors.",
            approx_usage_percent=8,
            confidence_model="key-colon-indent",
        ),
        FormatUsage(
            format="toml",
            variant=None,
            extensions=(".toml",),
            mime_hints=("application/toml", "text/plain"),
            context="Structured key/value configuration with sections; used by Python, Rust, and modern toolchains.",
            approx_usage_percent=4,
            confidence_model="bracket-section+eq-sign",
        ),
        FormatUsage(
            format="ini",
            variant="sectioned-ini",
            extensions=(".ini", ".cfg", ".cnf"),
            mime_hints=("text/plain",),
            context="Classic INI-style configuration with [section] headers, key density analysis, and comment preservation cues.",
            approx_usage_percent=15,
            confidence_model="section-headers+key-density+extension",
            variants=(
                UsageVariant(
                    variant="sectioned-ini",
                    context="Traditional INI layout combining [section] headers with = assignments and comment markers.",
                ),
                UsageVariant(
                    variant="sectionless-ini",
                    context="INI-like payloads without [section] headers that still rely on dense key=value assignments.",
                ),
                UsageVariant(
                    variant="desktop-ini",
                    filename_hint="desktop.ini",
                    context="Shell metadata files authored by Windows Explorer that retain key ordering for icon and folder hints.",
                ),
                UsageVariant(
                    variant="java-properties",
                    extensions=(".properties",),
                    context="Sectionless Java properties exported with = or : separators and continuation backslashes.",
                ),
            ),
        ),
        FormatUsage(
            format="properties",
            variant="java-properties",
            extensions=(".properties",),
            mime_hints=("text/plain",),
            context="Java runtime configuration using = or : separators, escaped continuations, and comment markers shared with INI.",
            approx_usage_percent=3,
            confidence_model="extension+key-separator+continuation",
        ),
        FormatUsage(
            format="unix-conf",
            variant="directive-conf",
            extensions=(".conf",),
            mime_hints=("text/plain",),
            context="Directive-heavy server and agent configuration favouring Include/SetEnv/Option statements over [section] headers.",
            approx_usage_percent=2,
            confidence_model="directive-keywords+comment-style",
            variants=(
                UsageVariant(
                    variant="directive-conf",
                    context="Generic Unix-style directives detected via Include/Option/SetEnv statements and dense keyword blocks.",
                ),
                UsageVariant(
                    variant="apache-conf",
                    filename_hint="httpd.conf",
                    context="Apache HTTP Server directives that combine LoadModule, SetEnv, and <Directory> blocks.",
                ),
                UsageVariant(
                    variant="nginx-conf",
                    filename_hint="nginx.conf",
                    context="nginx configurations with server/location blocks and brace-delimited directives.",
                ),
            ),
        ),
        FormatUsage(
            format="script-config",
            variant="shell-automation",
            extensions=(".ps1", ".bat", ".cmd", ".vbs"),
            mime_hints=("text/x-powershell", "text/x-batch", "text/vbscript", "text/plain"),
            context="Script-based configuration or setup logic for command-shell automation.",
            approx_usage_percent=4,
            confidence_model="shebang+keyword-scan",
            variants=(
                UsageVariant(
                    variant="ps1-shell",
                    extensions=(".ps1",),
                    context="PS1 shell scripts used for automation and configuration.",
                ),
                UsageVariant(
                    variant="batch-script",
                    extensions=(".bat",),
                    context="Batch scripts for setup or environment configuration.",
                ),
                UsageVariant(
                    variant="cmd-shell",
                    extensions=(".cmd",),
                    context="CMD shell scripts handling deployment tasks.",
                ),
                UsageVariant(
                    variant="vbscript",
                    extensions=(".vbs",),
                    context="Legacy VBScript automation snippets maintained for compatibility.",
                ),
            ),
        ),
        FormatUsage(
            format="embedded-sql-db",
            variant=None,
            extensions=(".sqlite", ".db"),
            mime_hints=("application/vnd.sqlite3", "application/octet-stream"),
            context="Embedded database files used for local settings, caches, or history storage.",
            approx_usage_percent=2,
            confidence_model="magic-bytes",
        ),
        FormatUsage(
            format="binary-dat",
            variant=None,
            extensions=(".dat", ".bin"),
            mime_hints=("application/octet-stream",),
            context="Opaque binary configuration or resource files; common in games, antivirus, and proprietary tools.",
            approx_usage_percent=3,
            confidence_model="entropy-check",
        ),
        FormatUsage(
            format="markdown-config",
            variant="embedded-yaml-frontmatter",
            extensions=(".md",),
            mime_hints=("text/markdown",),
            context="Markdown documents containing YAML front matter used by static site generators.",
            approx_usage_percent=0.5,
            confidence_model="yaml-frontmatter",
        ),
        FormatUsage(
            format="plist",
            variant="xml-or-binary",
            extensions=(".plist",),
            mime_hints=("application/x-plist", "application/xml"),
            context="Property list format used by desktop and mobile tooling, occasionally present on Windows cross-ports.",
            approx_usage_percent=0.5,
            confidence_model="header-magic+xml-declaration",
        ),
        FormatUsage(
            format="ini-json-hybrid",
            variant="section-json-hybrid",
            extensions=(".ini",),
            mime_hints=("text/plain",),
            context="INI payloads that interleave [section] headers with JSON braces for nested metadata blocks.",
            approx_usage_percent=0.5,
            confidence_model="section-headers+json-braces",
        ),
        FormatUsage(
            format="env-file",
            variant="dotenv",
            extensions=(".env",),
            mime_hints=("text/plain",),
            context="Environment variable definitions with export prefixes, KEY=VALUE density, and minimal directives.",
            approx_usage_percent=1,
            confidence_model="dotenv-filenames+export-or-equals",
            variants=(
                UsageVariant(
                    variant="dotenv",
                    context=".env-style files combining KEY=VALUE lines with optional export prefixes and comment preservation.",
                ),
            ),
        ),
    ),
    meta=SurveyMeta(
        primary_key="format",
        secondary_key="variant",
        total_formats=18,
        usage_sum_percent=100,
        notes=(
            "Usage shares are approximate and rounded to 0.5%.",
            "Confidence models indicate dominant detection strategy for parsers.",
            "Variants provide finer identification for context-specific tools (e.g., environment-focused JSON vs generic JSON).",
            "Dataset normalized for Windows-dominant ecosystems but applicable to cross-platform ports.",
        ),
    ),
)


__all__ = [
    "DETECTION_CATALOG",
    "FORMAT_SURVEY",
    "ContentSignature",
    "FormatClass",
    "FormatSubtype",
    "RemediationHint",
]
