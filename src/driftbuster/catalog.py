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
    filename_patterns: Tuple[str, ...] = field(default_factory=tuple)
    content_signatures: Tuple[ContentSignature, ...] = field(default_factory=tuple)
    mime_hints: Tuple[str, ...] = field(default_factory=tuple)


@dataclass(frozen=True)
class FormatClass:
    """Primary detection class definition."""

    name: str
    priority: int
    extensions: Tuple[str, ...]
    filename_patterns: Tuple[str, ...] = field(default_factory=tuple)
    content_signatures: Tuple[ContentSignature, ...] = field(default_factory=tuple)
    mime_hints: Tuple[str, ...] = field(default_factory=tuple)
    examples: Tuple[str, ...] = field(default_factory=tuple)
    subtypes: Tuple[FormatSubtype, ...] = field(default_factory=tuple)


@dataclass(frozen=True)
class FallbackClass:
    name: str
    priority: int
    mime_hints: Tuple[str, ...]


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
            priority=10,
            extensions=(".reg",),
            filename_patterns=("(?i)^.*\\.reg$",),
            content_signatures=(
                ContentSignature(
                    type="starts_with_regex",
                    pattern="^(Windows Registry Editor Version (4|5)\\.00|REGEDIT4)\\r?\\n",
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^\\[HKEY_(LOCAL_MACHINE|CURRENT_USER|CLASSES_ROOT|USERS|CURRENT_CONFIG)\\\\.+\\]$",
                ),
            ),
            mime_hints=("application/regedit", "text/plain"),
            examples=(
                "Windows Registry Editor Version 5.00\\n[HKEY_CURRENT_USER\\Software\\Vendor\\App]\\n\"Key\"=\"Value\"",
            ),
        ),
        FormatClass(
            name="RegistryLive",
            priority=15,
            extensions=(".json", ".yml", ".yaml"),
            filename_patterns=(
                "(?i)^.*\\.(regscan\\.json|registry\\.json)$",
                "(?i)^(registry|reg).*\\.(json|ya?ml)$",
            ),
            content_signatures=(
                ContentSignature(
                    type="contains_regex",
                    pattern='\"registry_scan\"\s*:\\s*\{',
                    optional=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^\\s*registry_scan\\s*:\\s*$",
                    multiline=True,
                    optional=True,
                ),
            ),
            mime_hints=("application/json", "text/yaml", "text/plain"),
            examples=(
                '{"registry_scan": {"token": "Vendor App", "keywords": ["server"]}}',
                'registry_scan:\n  token: Vendor App\n  keywords: [server]',
            ),
        ),
        FormatClass(
            name="StructuredConfigXml",
            priority=20,
            extensions=(".config",),
            filename_patterns=("(?i)^.*\\.(config)$", "(?i)^(app|web|machine)\\.config$"),
            content_signatures=(
                ContentSignature(type="contains_regex", pattern="<configuration(\\s|>)"),
                ContentSignature(type="contains_regex", pattern="<(appSettings|runtime|system\\.web)(\\s|>)"),
            ),
            mime_hints=("application/xml", "text/xml"),
        ),
        FormatClass(
            name="XmlGeneric",
            priority=30,
            extensions=(".xml", ".manifest", ".resx", ".xaml"),
            filename_patterns=("(?i)^.*\\.(xml|manifest|resx|xaml)$",),
            content_signatures=(
                ContentSignature(
                    type="starts_with_regex",
                    pattern="^\\s*<\\?xml\\s+version\\s*=\\s*\"[^\"]+\"",
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^\\s*<[^!?][\\w:.-]+(\\s|>)",
                ),
            ),
            mime_hints=("application/xml", "text/xml"),
            subtypes=(
                FormatSubtype(
                    name="WindowsManifestXml",
                    priority=31,
                    filename_patterns=("(?i)^.*\\.manifest$",),
                    content_signatures=(
                        ContentSignature(type="contains_regex", pattern="<assembly(\\s|>)"),
                        ContentSignature(
                            type="contains_regex",
                            pattern="xmlns=\"urn:schemas-microsoft-com:asm\\.v1\"",
                        ),
                    ),
                ),
                FormatSubtype(
                    name="ResxXml",
                    priority=32,
                    filename_patterns=("(?i)^.*\\.resx$",),
                    content_signatures=(
                        ContentSignature(type="contains_regex", pattern="<root(\\s|>)"),
                        ContentSignature(type="contains_regex", pattern="<data\\s+name=\""),
                    ),
                ),
                FormatSubtype(
                    name="XamlUiXml",
                    priority=33,
                    filename_patterns=("(?i)^.*\\.xaml$",),
                    content_signatures=(
                        ContentSignature(
                            type="contains_regex",
                            pattern="xmlns(:\\w+)?=\"http://schemas\\.microsoft\\.com/winfx/2006/xaml\"",
                        ),
                        ContentSignature(
                            type="contains_regex",
                            pattern="<(Window|UserControl|Page|Application|ResourceDictionary)(\\s|>)",
                        ),
                    ),
                ),
            ),
        ),
        FormatClass(
            name="Json",
            priority=40,
            extensions=(".json", ".jsonc"),
            filename_patterns=("(?i)^.*\\.(json|jsonc)$",),
            content_signatures=(
                ContentSignature(type="starts_with_regex", pattern="^\\s*[\\[{]"),
                ContentSignature(
                    type="not_contains_regex",
                    pattern="\\/\\/|/\\*",
                    optional=True,
                ),
                ContentSignature(type="json_parse_probe", max_bytes=2_097_152),
            ),
            mime_hints=("application/json",),
            subtypes=(
                FormatSubtype(
                    name="JsonWithComments",
                    priority=41,
                    filename_patterns=("(?i)^.*\\.jsonc$",),
                    content_signatures=(
                        ContentSignature(
                            type="contains_regex",
                            pattern="(^|\\n)\\s*(\\/\\/|/\\*)",
                        ),
                    ),
                    mime_hints=("application/json", "text/plain"),
                ),
                FormatSubtype(
                    name="StructuredSettingsJson",
                    priority=42,
                    filename_patterns=("(?i)^appsettings(\\.[A-Za-z0-9_-]+)?\\.json$",),
                    content_signatures=(
                        ContentSignature(
                            type="contains_regex",
                            pattern='"Logging"\\s*:\\s*\\{',
                        ),
                        ContentSignature(
                            type="contains_regex",
                            pattern='"ConnectionStrings"\\s*:\\s*\\{',
                            optional=True,
                        ),
                    ),
                ),
            ),
        ),
        FormatClass(
            name="Yaml",
            priority=50,
            extensions=(".yml", ".yaml"),
            filename_patterns=("(?i)^.*\\.(ya?ml)$",),
            content_signatures=(
                ContentSignature(
                    type="starts_with_regex",
                    pattern="^\\s*---\\s*$",
                    optional=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^[ \\t]*[A-Za-z0-9_\\-\"']+[ \\t]*:(\\s|$)",
                    multiline=True,
                ),
            ),
            mime_hints=("application/yaml", "text/yaml", "text/plain"),
        ),
        FormatClass(
            name="Toml",
            priority=60,
            extensions=(".toml",),
            filename_patterns=("(?i)^.*\\.toml$",),
            content_signatures=(
                ContentSignature(
                    type="contains_regex",
                    pattern="^\\s*\\[[A-Za-z0-9_.\\-]+\\]\\s*$",
                    multiline=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^[A-Za-z0-9_\\-]+\\s*=\\s*[^\\n]+$",
                    multiline=True,
                ),
            ),
            mime_hints=("application/toml", "text/plain"),
            subtypes=(
                FormatSubtype(
                    name="PackageManifestToml",
                    priority=61,
                    filename_patterns=("^Cargo\\.toml$",),
                ),
                FormatSubtype(
                    name="ProjectSettingsToml",
                    priority=62,
                    filename_patterns=("^pyproject\\.toml$",),
                ),
            ),
        ),
        FormatClass(
            name="Ini",
            priority=70,
            extensions=(".ini", ".cfg", ".cnf"),
            filename_patterns=("(?i)^.*\\.(ini|cfg|cnf)$", "(?i)^desktop\\.ini$"),
            content_signatures=(
                ContentSignature(
                    type="contains_regex",
                    pattern="^\\s*\\[[^\\]\\n]+\\]\\s*$",
                    multiline=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^[A-Za-z0-9_.\\-]+\\s*=\\s*[^\\n]*$",
                    multiline=True,
                ),
            ),
            mime_hints=("text/plain",),
        ),
        FormatClass(
            name="KeyValueProperties",
            priority=80,
            extensions=(".properties",),
            filename_patterns=("(?i)^.*\\.properties$",),
            content_signatures=(
                ContentSignature(
                    type="contains_regex",
                    pattern="^[#!].*$",
                    multiline=True,
                    optional=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^[A-Za-z0-9_.\\-]+\\s*(=|:)\\s*.*$",
                    multiline=True,
                ),
            ),
            mime_hints=("text/plain",),
        ),
        FormatClass(
            name="UnixConf",
            priority=90,
            extensions=(".conf",),
            filename_patterns=("(?i)^.*\\.conf$",),
            content_signatures=(
                ContentSignature(
                    type="contains_regex",
                    pattern="^(\\s*#|\\s*;|\\s*[A-Za-z0-9_.\\-]+\\s+[^\\n]+)$",
                    multiline=True,
                ),
            ),
            mime_hints=("text/plain",),
        ),
        FormatClass(
            name="ScriptConfig",
            priority=100,
            extensions=(".ps1", ".bat", ".cmd", ".vbs"),
            filename_patterns=("(?i)^.*\\.(ps1|bat|cmd|vbs)$",),
            content_signatures=(
                ContentSignature(
                    type="starts_with_regex",
                    pattern=r"^#requires|^Param\(",
                    optional=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="(?i)Set-Item|New-Item|Set-Content|Get-Item",
                    optional=True,
                ),
                ContentSignature(
                    type="contains_regex",
                    pattern="^(?:@?echo\\s+off|set\\s+\\w+=)",
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
        ),
        FormatClass(
            name="EmbeddedSqlDb",
            priority=110,
            extensions=(".sqlite", ".db"),
            filename_patterns=("(?i)^.*\\.(sqlite|db)$",),
            content_signatures=(
                ContentSignature(
                    type="binary_magic",
                    offset=0,
                    hex="53514C69746520666F726D6174203300",
                ),
            ),
            mime_hints=("application/vnd.sqlite3", "application/octet-stream"),
        ),
        FormatClass(
            name="GenericBinaryDat",
            priority=120,
            extensions=(".dat", ".bin"),
            filename_patterns=("(?i)^.*\\.(dat|bin)$",),
            content_signatures=(
                ContentSignature(
                    type="binary_threshold",
                    non_text_ratio_gt=0.25,
                    sample_bytes=16_384,
                ),
            ),
            mime_hints=("application/octet-stream",),
        ),
    ),
    fallback=FallbackClass(
        name="UnknownTextOrBinary",
        priority=1000,
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
            context="Enterprise XML configuration files (e.g., app.config, web.config, machine.config) used by general web frameworks.",
            approx_usage_percent=12,
            confidence_model="schema+namespace",
        ),
        FormatUsage(
            format="xml",
            variant="generic",
            extensions=(".xml", ".manifest", ".resx", ".xaml"),
            mime_hints=("application/xml", "text/xml"),
            context="Generic or declarative XML configuration; also includes system manifests, .resx resources, and XAML-style UI files.",
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


__all__ = ["DETECTION_CATALOG", "FORMAT_SURVEY", "ContentSignature", "FormatClass", "FormatSubtype"]
