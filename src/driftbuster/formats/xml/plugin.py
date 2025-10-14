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

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional, Tuple

from ..registry import register
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
_DOTNET_TRANSFORM_FILENAME = re.compile(
    r"^(?P<scope>web|app|machine)\.(?P<target>[^/\\]+)\.config$",
    re.IGNORECASE,
)
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
_START_TAG = re.compile(r"<(?P<name>[A-Za-z_][\w:.-]*)\b")
_XMLNS_ATTRIBUTE = re.compile(
    r"xmlns(?::(?P<prefix>[\w.-]+))?\s*=\s*(?P<quote>['\"])(?P<uri>.*?)(?P=quote)",
    re.DOTALL,
)
_DOCTYPE_DECL = re.compile(r"<!DOCTYPE\s+(?P<name>[\w:.-]+)", re.IGNORECASE)
_SCHEMA_LOCATION_ATTR = re.compile(
    r"(?i)xsi:schemaLocation\s*=\s*(?P<quote>['\"])(?P<value>.*?)(?P=quote)",
    re.DOTALL,
)
_NO_NAMESPACE_SCHEMA_ATTR = re.compile(
    r"(?i)xsi:noNamespaceSchemaLocation\s*=\s*(?P<quote>['\"])(?P<value>.*?)(?P=quote)",
    re.DOTALL,
)


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


def _parse_schema_locations(value: str) -> list[dict[str, str]]:
    cleaned = [segment for segment in re.split(r"\s+", value.strip()) if segment]
    pairs: list[dict[str, str]] = []
    for index in range(0, len(cleaned) - 1, 2):
        namespace = cleaned[index]
        location = cleaned[index + 1]
        pairs.append({"namespace": namespace, "location": location})
    return pairs


@dataclass
class XmlPlugin:
    name: str = "xml"
    priority: int = 100
    version: str = "0.0.2"

    def detect(self, path: Path, sample: bytes, text: Optional[str]) -> Optional[DetectionMatch]:
        if text is None:
            return None

        extension = path.suffix.lower()
        reasons: List[str] = []

        # Prefer .config specific detection first.
        metadata = self._collect_metadata(text)

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
        xml_extensions = {".xml", ".manifest", ".resx", ".xaml", ".config", ".xsl", ".xslt"}
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

        role, confidence, inferred_transform, transform_scope = self._detect_config_role(
            path,
            text,
            reasons,
            metadata,
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
        metadata: Dict[str, object],
    ) -> Tuple[str, float, bool, Optional[str]]:
        """Combine filename and section hints to classify `.config` roles.

        The helper returns a tuple ``(role, confidence, inferred_transform,
        transform_scope)`` while annotating ``metadata`` with filename-derived
        hints (for example ``config_transform_layers``).  Filename patterns such
        as ``web.config`` or ``app.Release.config`` set the baseline role before
        content-based hints (``<system.web>`` vs ``<startup>``) adjust the
        classification.  This ordering ensures transforms inherit the correct
        scope while generic configuration files fall back to ``"generic"`` when
        neither filenames nor section hints match.
        """

        role: Optional[str] = None
        confidence = 0.85
        inferred_transform = False
        transform_scope: Optional[str] = None

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
                target = transform_match.group("target")
                metadata.setdefault("config_transform_target", target)
                layers = [segment for segment in target.split(".") if segment]
                if layers:
                    metadata.setdefault("config_transform_layers", layers)
                    metadata.setdefault(
                        "config_transform_layers_normalised",
                        [segment.lower() for segment in layers],
                    )
                    metadata.setdefault("config_transform_environment", layers[-1])
                    metadata.setdefault(
                        "config_transform_environment_normalised",
                        layers[-1].lower(),
                    )
                    metadata.setdefault(
                        "config_transform_precedence",
                        " -> ".join(layers),
                    )
                if scope == "web":
                    role = "web"
                elif scope == "app":
                    role = "app"
                elif scope == "machine":
                    role = "machine"
                chain = layers if layers else [target]
                if chain:
                    readable_chain = " -> ".join(chain)
                    if len(chain) > 1:
                        message = f"Transform targets layered environments ({readable_chain})"
                    else:
                        message = f"Transform targets environment {readable_chain}"
                    self._add_reason(reasons, message)
                self._add_reason(
                    reasons,
                    "Filename pattern web|app|machine.*.config suggests a build-specific transform",
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

        return role, confidence, inferred_transform, transform_scope

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
        if isinstance(schema_locations, list) and schema_locations:
            count = len(schema_locations)
            if count == 1:
                entry = schema_locations[0]
                namespace = entry.get("namespace") if isinstance(entry, dict) else None
                if namespace:
                    self._add_reason(reasons, f"Found schema reference for namespace {namespace}")
                else:
                    self._add_reason(reasons, "Found schema reference for default namespace")
            else:
                self._add_reason(reasons, f"Found schema references for {count} namespaces")
        if metadata.get("schema_no_namespace_location"):
            self._add_reason(reasons, "Detected no-namespace schema reference")

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
            bonus += 0.01
        if metadata.get("schema_no_namespace_location"):
            bonus += 0.01
        return bonus

    def _collect_metadata(self, text: str) -> Dict[str, object]:
        snippet = text[:4096]
        metadata: Dict[str, object] = {}

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

        schema_locations: list[dict[str, str]] = []
        schema_namespaces: list[str] = []
        for match in _SCHEMA_LOCATION_ATTR.finditer(snippet):
            raw_value = match.group("value")
            parsed = _parse_schema_locations(raw_value)
            for entry in parsed:
                namespace = entry.get("namespace", "")
                if entry not in schema_locations:
                    schema_locations.append(entry)
                if namespace and namespace not in schema_namespaces:
                    schema_namespaces.append(namespace)
        if schema_locations:
            metadata["schema_locations"] = schema_locations
            metadata["schema_location_namespaces"] = schema_namespaces

        no_namespace_match = _NO_NAMESPACE_SCHEMA_ATTR.search(snippet)
        if no_namespace_match:
            metadata["schema_no_namespace_location"] = no_namespace_match.group("value").strip()

        namespace_matches = {
            (m.group("prefix") or "default"): m.group("uri") for m in _XMLNS_ATTRIBUTE.finditer(snippet)
        }
        if namespace_matches:
            metadata["namespaces"] = namespace_matches
            root_prefix = metadata.get("root_prefix")
            if isinstance(root_prefix, str):
                ns = namespace_matches.get(root_prefix)
                if ns:
                    metadata["root_namespace"] = ns
            elif namespace_matches.get("default"):
                metadata["root_namespace"] = namespace_matches["default"]

        return metadata

    def _extract_root_attributes(self, snippet: str, start_index: int) -> Dict[str, str]:
        attributes: Dict[str, str] = {}
        if ">" not in snippet[start_index:]:
            return attributes
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
            return attributes
        if raw_segment.endswith("/"):
            raw_segment = raw_segment[:-1].rstrip()
        for attr in _XML_DECLARATION_ATTR.finditer(raw_segment):
            name = attr.group("name")
            value = attr.group("value")
            attributes[name] = value
        return attributes


register(XmlPlugin())
