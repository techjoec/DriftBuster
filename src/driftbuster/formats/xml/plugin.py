"""XML detection heuristics for configuration-style and generic documents.

This module favours deterministic pattern checks so manual auditors can reason
about each decision.  Detection follows a strict order:

1. Classify framework configuration payloads using filename patterns and core
   section hints.  Transform files (``web.Release.config`` etc.) are surfaced
   before generic vendor fallbacks.
2. Inspect namespaces for common application manifest, ``.resx``, and XAML
   variants.  These namespace detections run before file-extension fallbacks so
   embedded XML payloads inside non-standard filenames still gain metadata.
3. Finally, fall back to extension- and structure-based hints for everything
   else while capturing the root element and namespace metadata for reporting
   adapters.

Ambiguous cases (custom namespaces, unusual vendor schemas) remain tagged as
``generic`` variants.  The heuristics intentionally lean on namespace presence
to avoid mis-classifying bespoke payloads; downstream adapters can inspect the
captured ``root_namespace`` and ``root_local_name`` fields when a manual review
is required.
"""

from __future__ import annotations

import hashlib
import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import ClassVar, Dict, List, Optional, Set, Tuple

try:  # pragma: no cover - optional hardened parser
    from defusedxml import ElementTree as DEFUSED_ET  # type: ignore
    from defusedxml.common import DefusedXmlException  # type: ignore
except ImportError:  # pragma: no cover - optional hardened parser
    DEFUSED_ET = None  # type: ignore[assignment]

    class DefusedXmlException(Exception):
        """Fallback exception type when defusedxml is unavailable."""

from ..format_registry import register
from ...core.types import DetectionMatch

_XML_DECLARATION = re.compile(r"^\s*<\?xml\b(?P<attrs>[^?>]*)\?>", re.IGNORECASE)
_XML_DECLARATION_ATTR = re.compile(
    r"(?P<name>[\w:.-]+)\s*=\s*(?P<quote>['\"])(?P<value>.*?)(?P=quote)", re.DOTALL
)
_GENERIC_ELEMENT = re.compile(r"^\s*<[^!?][\w:.-]+(\s|>)", re.MULTILINE)
_CONFIG_CONFIGURATION = re.compile(
    r"<(?:[A-Za-z_][\w:.-]*:)?configuration(\s|>)",
    re.IGNORECASE,
)
_CONFIG_SECTIONS = re.compile(r"<(appSettings|runtime|system\.web)(\s|>)", re.IGNORECASE)
_DOTNET_WEB_HINT = re.compile(r"<(system\.web|system\.webServer)(\s|>)", re.IGNORECASE)
_DOTNET_APP_HINT = re.compile(r"<(startup|supportedRuntime|assemblyBinding)(\s|>)", re.IGNORECASE)
_DOTNET_WEB_FILENAME = re.compile(r"^web\.config$", re.IGNORECASE)
_DOTNET_APP_FILENAME = re.compile(r"^app\.config$", re.IGNORECASE)
_DOTNET_MACHINE_FILENAME = re.compile(r"^machine\.config$", re.IGNORECASE)
_DOTNET_TRANSFORM_FILENAME = re.compile(r"^(?P<scope>web|app)\.[^/\\]+\.config$", re.IGNORECASE)
_DOTNET_ASSEMBLY_CONFIG = re.compile(r"\.(?:exe|dll)\.config$", re.IGNORECASE)
_XDT_NAMESPACE_DECL = re.compile(
    r"xmlns:xdt\s*=\s*(?P<quote>['\"])(?P<uri>http://schemas\.microsoft\.com/XML-Document-Transform)(?P=quote)",
    re.IGNORECASE,
)
_XDT_TRANSFORM_ATTR = re.compile(r"xdt:Transform\s*=\s*(?:['\"][^'\"]+['\"])")


_MANIFEST_NAMESPACE = re.compile(r"urn:schemas-microsoft-com:asm\.v1", re.IGNORECASE)
_RESX_SCHEMA = re.compile(r"http://schemas\.microsoft\.com/.*resx", re.IGNORECASE)
_XAML_NAMESPACE = re.compile(r"http://schemas\.microsoft\.com/winfx/2006/xaml", re.IGNORECASE)
_XSLT_NAMESPACE = re.compile(r"http://www\.w3\.org/1999/XSL/Transform", re.IGNORECASE)
_MSBUILD_NAMESPACE = re.compile(
    r"http://schemas\.microsoft\.com/developer/msbuild/2003",
    re.IGNORECASE,
)
_START_TAG = re.compile(r"<(?P<name>[A-Za-z_][\w:.-]*)\b")
_XMLNS_ATTRIBUTE = re.compile(
    r"xmlns(?::(?P<prefix>[\w.-]+))?\s*=\s*(?P<quote>['\"])(?P<uri>.*?)(?P=quote)",
    re.DOTALL,
)
_DOCTYPE_DECL = re.compile(r"<!DOCTYPE\s+(?P<name>[\w:.-]+)", re.IGNORECASE)
_ENTITY_DECL = re.compile(r"<!ENTITY", re.IGNORECASE)


_VENDOR_CONFIG_ROOTS: Dict[str, Tuple[str, str, str, float]] = {
    "nlog": (
        "structured-config-xml",
        "nlog-config",
        "Root element indicates NLog logging configuration",
        0.82,
    ),
    "log4net": (
        "structured-config-xml",
        "log4net-config",
        "Root element indicates log4net logging configuration",
        0.82,
    ),
    "serilog": (
        "structured-config-xml",
        "serilog-config",
        "Root element indicates Serilog logging configuration",
        0.82,
    ),
}


def _split_qualified_name(name: str) -> tuple[Optional[str], str]:
    if ":" in name:
        prefix, local = name.split(":", 1)
        if prefix and local:
            return prefix, local
    return None, name


@dataclass
class XmlPlugin:
    name: str = "xml"
    priority: int = 100
    version: str = "0.0.4"
    _MAX_SAFE_PARSE_BYTES: ClassVar[int] = 512 * 1024

    def detect(self, path: Path, sample: bytes, text: Optional[str]) -> Optional[DetectionMatch]:
        if text is None:
            return None

        extension = path.suffix.lower()
        reasons: List[str] = []

        # Prefer .config specific detection first.
        metadata = self._collect_metadata(text, extension=extension)

        if extension == ".config":
            config_root = False
            if _CONFIG_CONFIGURATION.search(text):
                reasons.append("Found <configuration> root element")
                config_root = True
            else:
                root_local = metadata.get("root_local_name")
                if isinstance(root_local, str) and root_local.lower() == "configuration":
                    reasons.append("Root element indicates framework configuration layout")
                    config_root = True
            if config_root:
                if _CONFIG_SECTIONS.search(text):
                    reasons.append("Matched known configuration section tags used by web frameworks")
                root = metadata.get("root_tag")
                if root:
                    self._add_reason(reasons, f"Detected root element <{root}>")
                self._append_declaration_reasons(metadata, reasons)
                self._append_namespace_reason(metadata, reasons)
                self._append_schema_reason(metadata, reasons)
                self._append_resx_reason(metadata, reasons)
                self._append_msbuild_reasons(metadata, reasons)
                self._append_attribute_hint_reasons(metadata, reasons)
                self._append_doctype_reason(metadata, reasons)
                variant, base_confidence = self._classify_config_variant(
                    path,
                    text,
                    reasons,
                    metadata,
                )
                confidence = min(
                    0.95,
                    base_confidence + self._confidence_bonus(metadata, found_elements=True),
                )
                return DetectionMatch(
                    plugin_name=self.name,
                    format_name="structured-config-xml",
                    variant=variant,
                    confidence=confidence,
                    reasons=reasons,
                    metadata=metadata or None,
                )

        # General XML heuristics.
        xml_extensions = {
            ".xml",
            ".manifest",
            ".resx",
            ".xaml",
            ".config",
            ".xsl",
            ".xslt",
            ".targets",
        }
        element_match = _GENERIC_ELEMENT.search(text)
        has_xml_declaration = bool(_XML_DECLARATION.search(text))
        if extension in xml_extensions or has_xml_declaration or element_match:
            if extension in xml_extensions:
                reasons.append(f"File extension {extension} suggests XML content")
            self._append_declaration_reasons(metadata, reasons)
            if element_match:
                reasons.append("Found XML element structure")
            format_name, variant, base_confidence = self._guess_variant(
                extension,
                text,
                reasons,
                metadata,
            )
            if root := metadata.get("root_tag"):
                reasons.append(f"Detected root element <{root}>")
            self._append_namespace_reason(metadata, reasons)
            self._append_schema_reason(metadata, reasons)
            self._append_resx_reason(metadata, reasons)
            self._append_msbuild_reasons(metadata, reasons)
            self._append_attribute_hint_reasons(metadata, reasons)
            self._append_doctype_reason(metadata, reasons)
            bonus = self._confidence_bonus(
                metadata,
                found_elements=bool(element_match) or bool(metadata.get("root_tag")),
            )
            return DetectionMatch(
                plugin_name=self.name,
                format_name=format_name,
                variant=variant,
                confidence=min(0.95, base_confidence + bonus),
                reasons=reasons or ["File extension indicates XML"],
                metadata=metadata or None,
            )
        return None

    def _guess_variant(
        self,
        extension: str,
        text: str,
        reasons: List[str],
        metadata: Dict[str, object],
    ) -> tuple[str, str, float]:
        namespaces = metadata.get("namespaces")
        if isinstance(namespaces, dict):
            root_namespace = metadata.get("root_namespace")
            if root_namespace:
                if _MANIFEST_NAMESPACE.search(root_namespace):
                    reasons.append("Matched assembly manifest namespace")
                    return "xml", "app-manifest-xml", 0.82
                if _RESX_SCHEMA.search(root_namespace):
                    reasons.append("Detected .resx schema reference")
                    return "xml", "resource-xml", 0.82
                if _XAML_NAMESPACE.search(root_namespace):
                    reasons.append("Found XAML namespace declaration")
                    return "xml", "interface-xml", 0.82
            if namespaces.get("default"):
                default_ns = namespaces["default"]
                if _MANIFEST_NAMESPACE.search(default_ns):
                    reasons.append("Matched assembly manifest namespace")
                    return "xml", "app-manifest-xml", 0.8
                if _RESX_SCHEMA.search(default_ns):
                    reasons.append("Detected .resx schema reference")
                    return "xml", "resource-xml", 0.8
                if _XAML_NAMESPACE.search(default_ns):
                    reasons.append("Found XAML namespace declaration")
                    return "xml", "interface-xml", 0.8
        if self._looks_like_msbuild(extension, metadata):
            kind = metadata.get("msbuild_kind") or self._classify_msbuild_kind(extension)
            metadata.setdefault("msbuild_detected", True)
            metadata.setdefault("msbuild_kind", kind)
            reason_map = {
                "targets": "Root element <Project> indicates an MSBuild targets layout",
                "props": "Root element <Project> indicates an MSBuild props layout",
                "project": "Root element <Project> indicates an MSBuild project definition",
            }
            base_confidence_map = {
                "targets": 0.83,
                "props": 0.82,
                "project": 0.83,
            }
            variant_map = {
                "targets": "msbuild-targets",
                "props": "msbuild-props",
                "project": "msbuild-project",
            }
            self._add_reason(reasons, reason_map.get(kind, reason_map["project"]))
            base_confidence = base_confidence_map.get(kind, 0.82)
            variant = variant_map.get(kind, "msbuild-project")
            return "xml", variant, base_confidence

        manifest_match = _MANIFEST_NAMESPACE.search(text)
        if extension == ".manifest" or manifest_match:
            if manifest_match:
                reasons.append("Matched assembly manifest namespace")
            return "xml", "app-manifest-xml", 0.8
        resx_match = _RESX_SCHEMA.search(text)
        if extension == ".resx" or resx_match:
            if resx_match:
                reasons.append("Detected .resx schema reference")
            return "xml", "resource-xml", 0.8
        xaml_match = _XAML_NAMESPACE.search(text)
        if extension == ".xaml" or xaml_match:
            if xaml_match:
                reasons.append("Found XAML namespace declaration")
            return "xml", "interface-xml", 0.8
        xslt_match = _XSLT_NAMESPACE.search(text)
        if extension in {".xsl", ".xslt"} or xslt_match:
            if extension in {".xsl", ".xslt"}:
                self._add_reason(
                    reasons,
                    f"File extension {extension} is commonly used for XSLT stylesheets",
                )
            if xslt_match:
                self._add_reason(
                    reasons,
                    "Detected XSLT namespace declaration",
                )
            root_local = metadata.get("root_local_name")
            if isinstance(root_local, str) and root_local.lower() in {"stylesheet", "transform"}:
                self._add_reason(
                    reasons,
                    f"Root element <{root_local}> indicates an XSLT stylesheet",
                )
            metadata.setdefault("xslt_stylesheet", True)
            return "xml", "xslt-xml", 0.82
        root_local = metadata.get("root_local_name")
        if isinstance(root_local, str) and root_local.lower() == "configuration":
            self._add_reason(
                reasons,
                "Root element indicates framework configuration layout",
            )
            variant, base_confidence = self._classify_config_variant(
                None,
                text,
                reasons,
                metadata,
            )
            return "structured-config-xml", variant, base_confidence
        if isinstance(root_local, str):
            vendor = _VENDOR_CONFIG_ROOTS.get(root_local.lower())
            if vendor:
                format_name, variant, vendor_reason, confidence = vendor
                self._add_reason(reasons, vendor_reason)
                return format_name, variant, confidence
        if extension == ".config":
            if root := metadata.get("root_tag"):
                reasons.append(
                    f"Root element <{root}> is not the standard <configuration>"
                )
            reasons.append("Treating as vendor-specific .config XML")
            return "structured-config-xml", "custom-config-xml", 0.7
        return "xml", "generic", 0.65

    @staticmethod
    def _add_reason(reasons: List[str], message: str) -> None:
        if message not in reasons:
            reasons.append(message)

    def _classify_config_variant(
        self,
        path: Optional[Path],
        text: str,
        reasons: List[str],
        metadata: Dict[str, object],
    ) -> tuple[str, float]:
        """Derive a variant for framework configuration style XML files."""

        if path is not None:
            metadata.setdefault("config_original_filename", path.name)

        role, confidence, inferred_transform, transform_scope, transform_stages = self._detect_config_role(
            path,
            text,
            reasons,
        )

        transform_namespace = bool(_XDT_NAMESPACE_DECL.search(text))
        transform_attribute = bool(_XDT_TRANSFORM_ATTR.search(text))
        is_transform = inferred_transform or transform_namespace or transform_attribute

        if transform_namespace:
            self._add_reason(
                reasons,
                "Detected XML-Document-Transform namespace declaration (xdt)",
            )
        if transform_attribute:
            self._add_reason(
                reasons,
                "Found xdt:Transform attribute indicating config transform instructions",
            )
        if is_transform:
            metadata["config_transform"] = True
            scope = transform_scope or (role if role != "generic" else None)
            if scope:
                metadata["config_transform_scope"] = scope
            if transform_stages:
                cleaned_stages = [stage.strip() for stage in transform_stages if stage.strip()]
                if cleaned_stages:
                    metadata["config_transform_stages"] = cleaned_stages
                    metadata["config_transform_primary_stage"] = cleaned_stages[-1]
                    metadata["config_transform_stage_count"] = len(cleaned_stages)
                    if len(cleaned_stages) == 1:
                        self._add_reason(
                            reasons,
                            f"Filename stage '{cleaned_stages[0]}' indicates transform precedence",
                        )
                    elif len(cleaned_stages) > 1:
                        chain = " -> ".join(cleaned_stages)
                        self._add_reason(
                            reasons,
                            f"Transform stages applied in order: {chain}",
                        )
            confidence = max(confidence, 0.9)

        metadata.setdefault("config_role", role)

        variant_map = {
            "web": "web-config",
            "app": "app-config",
            "machine": "machine-config",
        }

        transform_variant_map = {
            "web": "web-config-transform",
            "app": "app-config-transform",
            "machine": "machine-config-transform",
        }

        if is_transform:
            return transform_variant_map.get(role, "config-transform"), confidence

        return variant_map.get(role, "web-or-app-config"), confidence

    def _detect_config_role(
        self,
        path: Optional[Path],
        text: str,
        reasons: List[str],
    ) -> Tuple[str, float, bool, Optional[str], List[str]]:
        """Combine filename and section hints to classify `.config` roles.

        The helper returns a tuple ``(role, confidence, inferred_transform,
        transform_scope, transform_stages)``.  Filename patterns such as ``web.config`` or
        ``app.Release.config`` set the baseline role before content-based hints
        (``<system.web>`` vs ``<startup>``) adjust the classification.  This
        ordering ensures transforms inherit the correct scope while generic
        configuration files fall back to ``"generic"`` when neither filenames
        nor section hints match.
        """

        role: Optional[str] = None
        confidence = 0.85
        inferred_transform = False
        transform_scope: Optional[str] = None
        transform_stages: List[str] = []

        filename = path.name if path is not None else ""
        lowered = filename.lower()
        if filename and _DOTNET_WEB_FILENAME.match(filename):
            self._add_reason(
                reasons,
                "Filename web.config strongly suggests web-hosted configuration",
            )
            role = "web"
            confidence = max(confidence, 0.9)
        elif filename and _DOTNET_APP_FILENAME.match(filename):
            self._add_reason(
                reasons,
                "Filename app.config indicates per-application configuration",
            )
            role = "app"
            confidence = max(confidence, 0.88)
        elif filename and _DOTNET_MACHINE_FILENAME.match(filename):
            self._add_reason(
                reasons,
                "Filename machine.config indicates machine-wide configuration",
            )
            role = "machine"
            confidence = max(confidence, 0.9)
        elif filename:
            transform_match = _DOTNET_TRANSFORM_FILENAME.match(filename)
            if transform_match:
                scope = transform_match.group("scope").lower()
                inferred_transform = True
                transform_scope = scope
                base_name = filename[:-7] if filename.lower().endswith(".config") else filename
                parts = [segment for segment in base_name.split(".") if segment]
                if len(parts) > 1:
                    transform_stages = parts[1:]
                if scope == "web":
                    role = "web"
                elif scope == "app":
                    role = "app"
                self._add_reason(
                    reasons,
                    "Filename pattern web|app.*.config suggests a build-specific transform",
                )
                confidence = max(confidence, 0.88 if scope == "app" else 0.89)
            elif _DOTNET_ASSEMBLY_CONFIG.search(lowered):
                self._add_reason(
                    reasons,
                    "Filename ending with .exe.config or .dll.config typically ships beside framework binaries",
                )
                role = "app"
                confidence = max(confidence, 0.88)

        if role is None and _DOTNET_WEB_HINT.search(text):
            self._add_reason(
                reasons,
                "Detected web-specific sections such as <system.web> or <system.webServer>",
            )
            role = "web"
            confidence = max(confidence, 0.88)

        if role is None and _DOTNET_APP_HINT.search(text):
            self._add_reason(
                reasons,
                "Detected application configuration sections like <startup> or <supportedRuntime>",
            )
            role = "app"
            confidence = max(confidence, 0.86)

        if role is None:
            role = "generic"

        return role, confidence, inferred_transform, transform_scope, transform_stages

    def _append_declaration_reasons(self, metadata: Dict[str, object], reasons: List[str]) -> None:
        if "xml_declaration" not in metadata:
            return
        declaration = metadata.get("xml_declaration")
        self._add_reason(reasons, "Detected XML declaration")
        if isinstance(declaration, dict):
            version = declaration.get("version")
            if version:
                self._add_reason(reasons, f"XML version declared as {version}")
            encoding = declaration.get("encoding")
            if encoding:
                self._add_reason(reasons, f"XML declared encoding {encoding}")
            standalone = declaration.get("standalone")
            if standalone:
                self._add_reason(reasons, f"XML standalone flag is {standalone}")

    def _append_namespace_reason(self, metadata: Dict[str, object], reasons: List[str]) -> None:
        namespaces = metadata.get("namespaces")
        if not namespaces or not isinstance(namespaces, dict):
            return
        default_ns = namespaces.get("default")
        if default_ns:
            self._add_reason(reasons, f"Detected XML namespace declarations (default namespace {default_ns})")
        else:
            self._add_reason(reasons, "Detected XML namespace declarations")

    def _append_schema_reason(self, metadata: Dict[str, object], reasons: List[str]) -> None:
        schema_locations = metadata.get("schema_locations")
        if not schema_locations or not isinstance(schema_locations, list):
            return
        for entry in schema_locations:
            if not isinstance(entry, dict):
                continue
            location = entry.get("location")
            namespace = entry.get("namespace")
            if location and namespace:
                self._add_reason(
                    reasons,
                    f"Schema {location} declared for namespace {namespace}",
                )
            elif location:
                self._add_reason(
                    reasons,
                    f"Schema {location} declared for default namespace",
                )

    def _append_resx_reason(self, metadata: Dict[str, object], reasons: List[str]) -> None:
        resource_keys = metadata.get("resource_keys")
        if not resource_keys or not isinstance(resource_keys, list):
            return
        preview = metadata.get("resource_keys_preview")
        if isinstance(preview, str) and preview:
            self._add_reason(
                reasons,
                f"Captured resource keys from .resx payload (e.g., {preview})",
            )
        else:
            self._add_reason(reasons, "Captured resource keys from .resx payload")

    def _append_msbuild_reasons(self, metadata: Dict[str, object], reasons: List[str]) -> None:
        if not metadata.get("msbuild_detected"):
            return
        default_targets = metadata.get("msbuild_default_targets")
        if isinstance(default_targets, list) and default_targets:
            preview = ", ".join(default_targets[:3])
            self._add_reason(
                reasons,
                f"MSBuild default targets declared ({preview})",
            )
        tools_version = metadata.get("msbuild_tools_version")
        if isinstance(tools_version, str) and tools_version:
            self._add_reason(
                reasons,
                f"MSBuild ToolsVersion set to {tools_version}",
            )
        sdk = metadata.get("msbuild_sdk")
        if isinstance(sdk, str) and sdk:
            self._add_reason(reasons, f"MSBuild SDK specified ({sdk})")
        targets = metadata.get("msbuild_targets")
        if isinstance(targets, list) and targets:
            preview = ", ".join(targets[:3])
            self._add_reason(
                reasons,
                f"Captured MSBuild target declarations ({preview})",
            )
        imports = metadata.get("msbuild_import_hints")
        if isinstance(imports, list) and imports:
            self._add_reason(reasons, "Captured MSBuild import references")

    def _append_attribute_hint_reasons(
        self, metadata: Dict[str, object], reasons: List[str]
    ) -> None:
        hints = metadata.get("attribute_hints")
        if not hints or not isinstance(hints, dict):
            return
        mapping = {
            "connection_strings": "Captured connection string attribute hints",
            "service_endpoints": "Captured service endpoint attribute hints",
            "feature_flags": "Captured feature flag attribute hints",
        }
        for key, message in mapping.items():
            entries = hints.get(key)
            if entries and isinstance(entries, list):
                self._add_reason(reasons, message)

    def _append_doctype_reason(self, metadata: Dict[str, object], reasons: List[str]) -> None:
        doctype = metadata.get("doctype")
        if doctype:
            self._add_reason(reasons, f"Document declares DOCTYPE {doctype}")

    def _confidence_bonus(self, metadata: Dict[str, object], *, found_elements: bool) -> float:
        bonus = 0.0
        if "xml_declaration" in metadata:
            bonus += 0.05
        if found_elements:
            bonus += 0.05
        if metadata.get("root_tag"):
            bonus += 0.03
        if metadata.get("namespaces"):
            bonus += 0.02
        if metadata.get("doctype"):
            bonus += 0.02
        if metadata.get("root_attributes"):
            bonus += 0.01
        if metadata.get("config_transform"):
            bonus += 0.01
        if metadata.get("schema_locations"):
            bonus += 0.02
        if metadata.get("resource_keys"):
            bonus += 0.01
        hints = metadata.get("attribute_hints")
        if isinstance(hints, dict) and any(hints.get(category) for category in hints):
            bonus += 0.01
        if metadata.get("msbuild_detected"):
            if metadata.get("msbuild_default_targets"):
                bonus += 0.01
            if metadata.get("msbuild_sdk"):
                bonus += 0.005
            if metadata.get("msbuild_import_hints"):
                bonus += 0.01
            if metadata.get("msbuild_targets"):
                bonus += 0.01
        return bonus

    def _collect_metadata(self, text: str, *, extension: str) -> Dict[str, object]:
        snippet = text[:4096]
        metadata: Dict[str, object] = {}

        root_element: Optional[ET.Element] = None
        stripped = text.lstrip()
        allow_parse = bool(stripped)
        if allow_parse and len(stripped) > self._MAX_SAFE_PARSE_BYTES:
            allow_parse = False
        if allow_parse:
            scan_segment = stripped[: self._MAX_SAFE_PARSE_BYTES]
            if _DOCTYPE_DECL.search(scan_segment) or _ENTITY_DECL.search(scan_segment):
                allow_parse = False
        if allow_parse:
            try:
                if DEFUSED_ET is not None:
                    root_element = DEFUSED_ET.fromstring(stripped)
                else:
                    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
                    root_element = ET.fromstring(stripped, parser=parser)
            except (ET.ParseError, DefusedXmlException):
                root_element = None

        declaration_match = _XML_DECLARATION.search(snippet)
        if declaration_match:
            attrs_segment = declaration_match.group("attrs") or ""
            decl_attrs: Dict[str, str] = {}
            for attr in _XML_DECLARATION_ATTR.finditer(attrs_segment):
                name = attr.group("name").lower()
                value = attr.group("value")
                decl_attrs[name] = value
            metadata["xml_declaration"] = decl_attrs
            encoding = decl_attrs.get("encoding")
            if encoding:
                metadata.setdefault("encoding", encoding)

        doctype_match = _DOCTYPE_DECL.search(snippet)
        if doctype_match:
            metadata["doctype"] = doctype_match.group("name")

        for match in _START_TAG.finditer(snippet):
            name = match.group("name")
            if name.startswith("?") or name.startswith("!"):
                continue
            metadata.setdefault("root_tag", name)
            prefix, local = _split_qualified_name(name)
            if prefix:
                metadata.setdefault("root_prefix", prefix)
            metadata.setdefault("root_local_name", local)
            attributes = self._extract_root_attributes(snippet, match.end())
            if attributes:
                metadata.setdefault("root_attributes", attributes)
            break

        namespace_pairs = []
        for m in _XMLNS_ATTRIBUTE.finditer(snippet):
            prefix = m.group("prefix") or "default"
            uri = (m.group("uri") or "").strip()
            namespace_pairs.append((prefix, uri))
        if namespace_pairs:
            namespace_pairs.sort(key=lambda item: (item[0].lower(), item[0]))
            namespace_matches = {prefix: uri for prefix, uri in namespace_pairs}
            metadata["namespaces"] = namespace_matches
            root_prefix = metadata.get("root_prefix")
            if isinstance(root_prefix, str):
                ns = namespace_matches.get(root_prefix)
                if ns:
                    metadata["root_namespace"] = ns
            elif namespace_matches.get("default"):
                metadata["root_namespace"] = namespace_matches["default"]

        self._extract_schema_locations(metadata)

        if root_element is not None:
            self._extract_resx_keys(root_element, metadata)
            self._extract_attribute_hints(root_element, metadata)
            self._extract_msbuild_metadata(root_element, metadata, extension)
        elif self._looks_like_msbuild(extension, metadata):
            metadata["msbuild_detected"] = True
            metadata["msbuild_kind"] = self._classify_msbuild_kind(extension)

        return metadata

    def _extract_root_attributes(self, snippet: str, start_index: int) -> Dict[str, str]:
        items: List[Tuple[str, str]] = []
        if ">" not in snippet[start_index:]:
            return {}
        segment = []
        in_quote: Optional[str] = None
        for char in snippet[start_index:]:
            if in_quote:
                segment.append(char)
                if char == in_quote:
                    in_quote = None
                continue
            if char in {'"', "'"}:
                segment.append(char)
                in_quote = char
                continue
            if char == ">":
                break
            segment.append(char)
        raw_segment = "".join(segment).strip()
        if not raw_segment:
            return {}
        if raw_segment.endswith("/"):
            raw_segment = raw_segment[:-1].rstrip()
        for attr in _XML_DECLARATION_ATTR.finditer(raw_segment):
            name = attr.group("name")
            value = attr.group("value").strip()
            items.append((name, value))
        if not items:
            return {}
        items.sort(key=lambda entry: (entry[0].lower(), entry[0]))
        return {name: value for name, value in items}

    def _extract_schema_locations(self, metadata: Dict[str, object]) -> None:
        attributes = metadata.get("root_attributes")
        if not attributes or not isinstance(attributes, dict):
            return
        schema_entries: List[Dict[str, Optional[str]]] = []
        for attr_name, raw_value in attributes.items():
            if not isinstance(raw_value, str):
                continue
            local_name = attr_name.split(":", 1)[-1]
            cleaned_value = " ".join(raw_value.split())
            if not cleaned_value:
                continue
            if local_name == "schemaLocation":
                tokens = cleaned_value.split()
                if len(tokens) < 2:
                    continue
                for index in range(0, len(tokens) - 1, 2):
                    namespace = tokens[index]
                    location = tokens[index + 1]
                    schema_entries.append({"namespace": namespace, "location": location})
            elif local_name == "noNamespaceSchemaLocation":
                schema_entries.append({"namespace": None, "location": cleaned_value})
        if schema_entries:
            metadata["schema_locations"] = schema_entries

    def _extract_resx_keys(self, root: ET.Element, metadata: Dict[str, object]) -> None:
        root_tag = root.tag.split("}")[-1] if "}" in root.tag else root.tag
        if root_tag.lower() != "root":
            return
        namespaces = metadata.get("namespaces")
        root_namespace = metadata.get("root_namespace")
        namespace_hint = None
        if isinstance(root_namespace, str):
            namespace_hint = root_namespace
        elif isinstance(namespaces, dict):
            namespace_hint = namespaces.get("default")
        if not namespace_hint or not _RESX_SCHEMA.search(namespace_hint):
            return
        resource_keys: List[str] = []
        for element in root.iter():
            tag_local = element.tag.split("}")[-1] if "}" in element.tag else element.tag
            if tag_local.lower() != "data":
                continue
            name = element.attrib.get("name")
            if not name:
                continue
            resource_keys.append(name)
            if len(resource_keys) >= 10:
                break
        if resource_keys:
            metadata["resource_keys"] = resource_keys

            preview = ", ".join(resource_keys[:3])
            metadata.setdefault("resource_keys_preview", preview)

    def _extract_attribute_hints(self, root: ET.Element, metadata: Dict[str, object]) -> None:
        hints: Dict[str, List[Dict[str, object]]] = {
            "connection_strings": [],
            "service_endpoints": [],
            "feature_flags": [],
        }
        seen: Dict[str, Set[tuple[str, str, str, str]]] = {
            "connection_strings": set(),
            "service_endpoints": set(),
            "feature_flags": set(),
        }

        for element in root.iter():
            attributes = dict(element.attrib)
            if not attributes:
                continue
            element_name = element.tag.split("}")[-1] if "}" in element.tag else element.tag
            lower_to_actual = {name.lower(): name for name in attributes}

            key_attr_name: Optional[str] = None
            key_value: Optional[str] = None
            for candidate in ("name", "key", "id"):
                actual = lower_to_actual.get(candidate)
                if actual:
                    value = attributes.get(actual, "")
                    if value:
                        key_attr_name = actual
                        key_value = value
                        break

            connection_attr = lower_to_actual.get("connectionstring")
            if connection_attr:
                self._add_attribute_hint(
                    hints=hints,
                    seen=seen,
                    category="connection_strings",
                    element_name=element_name,
                    attribute_name=connection_attr,
                    value=attributes.get(connection_attr, ""),
                    key_value=key_value,
                    key_attribute=key_attr_name,
                )

            for candidate in ("address", "endpoint", "url", "uri", "baseaddress", "serviceurl"):
                attr_name = lower_to_actual.get(candidate)
                if not attr_name:
                    continue
                value = attributes.get(attr_name, "")
                if self._looks_like_endpoint(value):
                    self._add_attribute_hint(
                        hints=hints,
                        seen=seen,
                        category="service_endpoints",
                        element_name=element_name,
                        attribute_name=attr_name,
                        value=value,
                        key_value=key_value,
                        key_attribute=key_attr_name,
                    )

            value_attribute = lower_to_actual.get("value")
            if value_attribute and key_value and self._contains_endpoint_keyword(key_value):
                value = attributes.get(value_attribute, "")
                if self._looks_like_endpoint(value):
                    self._add_attribute_hint(
                        hints=hints,
                        seen=seen,
                        category="service_endpoints",
                        element_name=element_name,
                        attribute_name=value_attribute,
                        value=value,
                        key_value=key_value,
                        key_attribute=key_attr_name,
                    )

            feature_value: Optional[str] = None
            feature_attr_name: Optional[str] = None
            if key_value and self._contains_feature_keyword(key_value):
                for candidate in ("value", "enabled", "isenabled", "defaultvalue"):
                    attr_name = lower_to_actual.get(candidate)
                    if attr_name:
                        candidate_value = attributes.get(attr_name, "")
                        if candidate_value:
                            feature_value = candidate_value
                            feature_attr_name = attr_name
                            break
            elif self._contains_feature_keyword(element_name):
                for candidate in ("name", "key"):
                    actual = lower_to_actual.get(candidate)
                    if actual and not key_value:
                        key_attr_name = actual
                        key_value = attributes.get(actual)
                        break
                for candidate in ("value", "enabled", "isenabled", "defaultvalue"):
                    attr_name = lower_to_actual.get(candidate)
                    if attr_name:
                        candidate_value = attributes.get(attr_name, "")
                        if candidate_value:
                            feature_value = candidate_value
                            feature_attr_name = attr_name
                            break

            if feature_value:
                self._add_attribute_hint(
                    hints=hints,
                    seen=seen,
                    category="feature_flags",
                    element_name=element_name,
                    attribute_name=feature_attr_name or "value",
                    value=feature_value,
                    key_value=key_value,
                    key_attribute=key_attr_name,
                )

        filtered = {category: entries for category, entries in hints.items() if entries}
        if filtered:
            metadata["attribute_hints"] = filtered

    def _looks_like_msbuild(self, extension: str, metadata: Dict[str, object]) -> bool:
        lowered_extension = extension.lower()
        msbuild_extensions = {
            ".targets",
            ".props",
            ".csproj",
            ".fsproj",
            ".vbproj",
            ".vcxproj",
            ".vcproj",
            ".proj",
            ".msbuildproj",
        }
        if lowered_extension in msbuild_extensions:
            return True

        root_local = metadata.get("root_local_name")
        if not isinstance(root_local, str) or root_local.lower() != "project":
            return False

        namespace = metadata.get("root_namespace")
        if isinstance(namespace, str) and _MSBUILD_NAMESPACE.search(namespace):
            return True

        namespaces = metadata.get("namespaces")
        if isinstance(namespaces, dict):
            default_ns = namespaces.get("default")
            if isinstance(default_ns, str) and _MSBUILD_NAMESPACE.search(default_ns):
                return True

        root_attributes = metadata.get("root_attributes")
        if isinstance(root_attributes, dict):
            lowered_keys = {name.lower() for name in root_attributes}
            if {"defaulttargets", "toolsversion", "sdk"} & lowered_keys:
                return True

        return False

    def _classify_msbuild_kind(self, extension: str) -> str:
        lowered_extension = extension.lower()
        if lowered_extension == ".targets":
            return "targets"
        if lowered_extension == ".props":
            return "props"
        if lowered_extension in {
            ".csproj",
            ".fsproj",
            ".vbproj",
            ".vcxproj",
            ".vcproj",
            ".proj",
            ".msbuildproj",
        }:
            return "project"
        return "project"

    def _extract_msbuild_metadata(
        self,
        root: ET.Element,
        metadata: Dict[str, object],
        extension: str,
    ) -> None:
        if not self._looks_like_msbuild(extension, metadata):
            return

        metadata["msbuild_detected"] = True
        kind = self._classify_msbuild_kind(extension)
        metadata["msbuild_kind"] = kind

        attr_lookup = {
            name.lower(): value.strip()
            for name, value in root.attrib.items()
            if isinstance(value, str) and value.strip()
        }

        default_targets = attr_lookup.get("defaulttargets")
        if default_targets:
            targets = [token.strip() for token in default_targets.split(";") if token.strip()]
            if targets:
                metadata["msbuild_default_targets"] = targets

        tools_version = attr_lookup.get("toolsversion")
        if tools_version:
            metadata["msbuild_tools_version"] = tools_version

        sdk = attr_lookup.get("sdk")
        if sdk:
            metadata["msbuild_sdk"] = sdk

        target_names: List[str] = []
        seen_target_names: Set[str] = set()
        import_hints: List[Dict[str, object]] = []
        seen_imports: Set[tuple[str, str]] = set()

        for element in root.iter():
            local_name = element.tag.split("}")[-1] if "}" in element.tag else element.tag
            if local_name == "Target":
                name = element.attrib.get("Name")
                if name:
                    cleaned_name = name.strip()
                    lowered_name = cleaned_name.lower()
                    if cleaned_name and lowered_name not in seen_target_names:
                        seen_target_names.add(lowered_name)
                        if len(target_names) < 10:
                            target_names.append(cleaned_name)
            if local_name != "Import":
                continue

            for attribute in ("Project", "Sdk"):
                raw_value = element.attrib.get(attribute)
                if not raw_value:
                    continue
                cleaned_value = raw_value.strip()
                if not cleaned_value:
                    continue
                digest = hashlib.sha256(cleaned_value.encode("utf-8", "ignore")).hexdigest()
                dedupe_key = (attribute.lower(), digest)
                if dedupe_key in seen_imports:
                    continue
                seen_imports.add(dedupe_key)
                entry: Dict[str, object] = {
                    "attribute": attribute,
                    "hash": digest,
                    "length": len(cleaned_value),
                }
                condition_value = element.attrib.get("Condition")
                if condition_value:
                    cleaned_condition = condition_value.strip()
                    if cleaned_condition:
                        entry["condition_hash"] = hashlib.sha256(
                            cleaned_condition.encode("utf-8", "ignore")
                        ).hexdigest()
                        entry["condition_length"] = len(cleaned_condition)
                import_hints.append(entry)
                break

        if target_names:
            metadata["msbuild_targets"] = target_names

        if import_hints:
            metadata["msbuild_import_hints"] = import_hints

    def _add_attribute_hint(
        self,
        *,
        hints: Dict[str, List[Dict[str, object]]],
        seen: Dict[str, Set[tuple[str, str, str, str]]],
        category: str,
        element_name: str,
        attribute_name: str,
        value: str,
        key_value: Optional[str],
        key_attribute: Optional[str],
    ) -> None:
        cleaned = value.strip()
        if not cleaned:
            return
        digest = hashlib.sha256(cleaned.encode("utf-8", "ignore")).hexdigest()
        dedupe_key = (
            digest,
            (key_value or "").strip().lower(),
            attribute_name.lower(),
            element_name.lower(),
        )
        bucket = seen[category]
        if dedupe_key in bucket:
            return
        entry: Dict[str, object] = {
            "element": element_name,
            "attribute": attribute_name,
            "hash": digest,
            "length": len(cleaned),
        }
        if key_value:
            entry["key"] = key_value
        if key_attribute:
            entry["key_attribute"] = key_attribute
        hints[category].append(entry)
        bucket.add(dedupe_key)

    @staticmethod
    def _looks_like_endpoint(value: str) -> bool:
        cleaned = value.strip()
        if not cleaned:
            return False
        lowered = cleaned.lower()
        if "://" in cleaned:
            return True
        if cleaned.startswith("\\\\"):
            return True
        return lowered.startswith("net.tcp://") or lowered.startswith("sb://")

    @staticmethod
    def _contains_feature_keyword(text: str) -> bool:
        lowered = text.lower()
        return any(keyword in lowered for keyword in ("feature", "flag", "toggle"))

    @staticmethod
    def _contains_endpoint_keyword(text: str) -> bool:
        lowered = text.lower()
        return any(
            keyword in lowered
            for keyword in (
                "endpoint",
                "serviceurl",
                "baseaddress",
                "callback",
                "apiurl",
                "address",
            )
        )



register(XmlPlugin())
