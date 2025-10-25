from __future__ import annotations

import json
import re
import shutil
from dataclasses import dataclass
from hashlib import sha256
from pathlib import Path
from typing import Any, Callable, Mapping, Sequence, Tuple

import importlib.resources as resources

SECRET_RULES_RESOURCE = "secret_rules.json"


@dataclass(frozen=True)
class SecretDetectionRule:
    name: str
    pattern: re.Pattern[str]
    description: str | None = None


@dataclass(frozen=True)
class SecretFinding:
    path: str
    rule: str
    line: int
    snippet: str


@dataclass
class SecretDetectionContext:
    rules: Sequence[SecretDetectionRule]
    version: str
    ignore_rules: frozenset[str]
    ignore_patterns: Sequence[re.Pattern[str]]
    ignore_pattern_text: Sequence[str]
    findings: list[SecretFinding]
    rules_loaded: bool


_RULE_CACHE: tuple[SecretDetectionRule, ...] | None = None
_RULE_VERSION: str | None = None
_RULE_LOADED: bool | None = None


def _compile_ruleset_from_mapping(
    payload: Mapping[str, Any] | None,
) -> tuple[tuple[SecretDetectionRule, ...], str] | None:
    if not payload:
        return None
    if not isinstance(payload, Mapping):
        return None

    rules_payload = payload.get("rules")
    if not isinstance(rules_payload, Sequence):
        return None

    compiled_rules: list[SecretDetectionRule] = []
    for entry in rules_payload:
        if not isinstance(entry, Mapping):
            continue
        name = str(entry.get("name") or "").strip()
        pattern_text = entry.get("pattern")
        if not name or not pattern_text:
            continue
        flags_text = str(entry.get("flags") or "").lower()
        flags = 0
        if "i" in flags_text:
            flags |= re.IGNORECASE
        try:
            pattern = re.compile(str(pattern_text), flags)
        except re.error:
            continue
        compiled_rules.append(
            SecretDetectionRule(
                name=name,
                pattern=pattern,
                description=str(entry.get("description")) if entry.get("description") else None,
            )
        )

    if not compiled_rules:
        return None

    version = str(payload.get("version") or "")
    return tuple(compiled_rules), version


def compile_ruleset_from_mapping(
    payload: Mapping[str, Any] | None,
) -> tuple[tuple[SecretDetectionRule, ...], str] | None:
    """Public wrapper for compiling ad-hoc ruleset mappings."""

    return _compile_ruleset_from_mapping(payload)


def load_secret_rules() -> tuple[tuple[SecretDetectionRule, ...], str, bool]:
    global _RULE_CACHE, _RULE_VERSION, _RULE_LOADED
    if _RULE_CACHE is not None and _RULE_VERSION is not None and _RULE_LOADED is not None:
        return _RULE_CACHE, _RULE_VERSION, _RULE_LOADED

    try:
        resource = resources.files(__package__).joinpath(SECRET_RULES_RESOURCE)
    except FileNotFoundError:
        _RULE_CACHE = ()
        _RULE_VERSION = "none"
        _RULE_LOADED = False
        return _RULE_CACHE, _RULE_VERSION, _RULE_LOADED

    try:
        with resource.open("r", encoding="utf-8") as handle:
            payload = json.load(handle)
    except FileNotFoundError:
        _RULE_CACHE = ()
        _RULE_VERSION = "none"
        _RULE_LOADED = False
        return _RULE_CACHE, _RULE_VERSION, _RULE_LOADED

    compiled = _compile_ruleset_from_mapping(payload)
    if compiled is None:
        _RULE_CACHE = ()
        _RULE_VERSION = str(payload.get("version", "unknown"))
        _RULE_LOADED = True
        return _RULE_CACHE, _RULE_VERSION, _RULE_LOADED

    rules, version = compiled
    _RULE_CACHE = rules
    _RULE_VERSION = version or str(payload.get("version", "unknown"))
    _RULE_LOADED = True
    return _RULE_CACHE, _RULE_VERSION, _RULE_LOADED


def secret_option_values(value: Any) -> tuple[str, ...]:
    if not value:
        return ()
    if isinstance(value, str):
        parts = re.split(r"[\s,;]+", value)
        return tuple(part.strip() for part in parts if part and part.strip())
    if isinstance(value, Sequence):
        result: list[str] = []
        for item in value:
            if item is None:
                continue
            text = str(item).strip()
            if text:
                result.append(text)
        return tuple(result)
    return ()


def build_context(
    options: Mapping[str, Any] | None,
    secret_scanner: Mapping[str, Any] | None,
) -> SecretDetectionContext:
    ruleset_payload = None
    if secret_scanner and isinstance(secret_scanner, Mapping):
        ruleset_payload = secret_scanner.get("ruleset")

    compiled_from_config = _compile_ruleset_from_mapping(
        ruleset_payload if isinstance(ruleset_payload, Mapping) else None
    )
    if compiled_from_config is not None:
        rules, version = compiled_from_config
        loaded = bool(rules)
    else:
        rules, version, loaded = load_secret_rules()

    ignore_rule_values: set[str] = set()
    if options:
        ignore_rule_values.update(secret_option_values(options.get("secret_ignore_rules")))
    if secret_scanner:
        ignore_rule_values.update(secret_option_values(secret_scanner.get("ignore_rules")))

    pattern_sources: list[str] = []
    if options:
        pattern_sources.extend(secret_option_values(options.get("secret_ignore_patterns")))
    if secret_scanner:
        pattern_sources.extend(secret_option_values(secret_scanner.get("ignore_patterns")))

    ignore_pattern_text: list[str] = []
    ignore_patterns: list[re.Pattern[str]] = []
    seen_patterns: set[str] = set()
    for pattern_text in pattern_sources:
        if pattern_text in seen_patterns:
            continue
        seen_patterns.add(pattern_text)
        ignore_pattern_text.append(pattern_text)
        try:
            ignore_patterns.append(re.compile(pattern_text))
        except re.error:
            continue

    return SecretDetectionContext(
        rules=rules,
        version=version,
        ignore_rules=frozenset(ignore_rule_values),
        ignore_patterns=tuple(ignore_patterns),
        ignore_pattern_text=tuple(ignore_pattern_text),
        findings=[],
        rules_loaded=loaded and bool(rules),
    )


def manifest_secret_scanner(
    options: Mapping[str, Any],
    secret_scanner: Mapping[str, Any],
    context: SecretDetectionContext,
) -> Mapping[str, Any]:
    ignore_rules: set[str] = set()
    ignore_rules.update(secret_option_values(options.get("secret_ignore_rules")))
    ignore_rules.update(secret_option_values(secret_scanner.get("ignore_rules")))

    ignore_patterns: set[str] = set()
    ignore_patterns.update(secret_option_values(options.get("secret_ignore_patterns")))
    ignore_patterns.update(secret_option_values(secret_scanner.get("ignore_patterns")))

    return {
        "ignore_rules": sorted(ignore_rules),
        "ignore_patterns": sorted(ignore_patterns),
        "ruleset_version": context.version,
    }


def looks_binary(path: Path) -> bool:
    try:
        with path.open("rb") as handle:
            chunk = handle.read(1024)
    except OSError:
        return False
    return b"\0" in chunk


def hash_file(path: Path) -> str:
    digest = sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def copy_with_secret_filter(
    source: Path,
    destination: Path,
    *,
    display_path: str,
    context: SecretDetectionContext,
    log: Callable[[str], None],
    binary_detector: Callable[[Path], bool] | None = None,
) -> tuple[int, str]:
    destination.parent.mkdir(parents=True, exist_ok=True)

    if not context.rules_loaded or not context.rules:
        shutil.copy2(source, destination)
        size = destination.stat().st_size
        return size, hash_file(destination)

    detector = binary_detector or looks_binary
    if detector(source):
        shutil.copy2(source, destination)
        size = destination.stat().st_size
        return size, hash_file(destination)

    buffered_lines: list[str] = []
    sanitized_lines: list[str] | None = None
    sanitized_matches = 0

    with source.open("r", encoding="utf-8", errors="replace") as input_handle:
        for lineno, line in enumerate(input_handle, 1):
            working_line = line
            match_found = False

            while True:
                triggered_rule: SecretDetectionRule | None = None
                match_obj: re.Match[str] | None = None
                for rule in context.rules:
                    if rule.name in context.ignore_rules:
                        continue
                    match = rule.pattern.search(working_line)
                    if not match:
                        continue
                    if any(pattern.search(line) for pattern in context.ignore_patterns):
                        continue
                    triggered_rule = rule
                    match_obj = match
                    break

                if triggered_rule is None or match_obj is None:
                    break

                if sanitized_lines is None:
                    sanitized_lines = list(buffered_lines)

                start, end = match_obj.span()
                redacted_line = working_line[:start] + "[SECRET]" + working_line[end:]
                preview_line = redacted_line.rstrip("\n\r")
                masked_preview = preview_line[:120]
                if len(preview_line) > 120:
                    masked_preview = preview_line[:117] + "..."

                sanitized_matches += 1
                match_found = True
                context.findings.append(
                    SecretFinding(
                        path=display_path,
                        rule=triggered_rule.name,
                        line=lineno,
                        snippet=preview_line[:200],
                    )
                )
                log(
                    f"secret candidate redacted ({triggered_rule.name}) from {display_path}:{lineno} -> {masked_preview}"
                )

                working_line = redacted_line

                if start == end:
                    break

            if sanitized_lines is not None:
                sanitized_lines.append(working_line if match_found else line)
            else:
                buffered_lines.append(line)

    if sanitized_lines is None:
        shutil.copy2(source, destination)
        size = destination.stat().st_size
        return size, hash_file(destination)

    destination.write_text("".join(sanitized_lines), encoding="utf-8")
    try:
        shutil.copystat(source, destination, follow_symlinks=False)
    except OSError:
        pass

    log(f"scrubbed {sanitized_matches} potential secret line(s) from {display_path}")

    size = destination.stat().st_size
    return size, hash_file(destination)


__all__ = [
    "SECRET_RULES_RESOURCE",
    "SecretDetectionContext",
    "SecretDetectionRule",
    "SecretFinding",
    "compile_ruleset_from_mapping",
    "build_context",
    "copy_with_secret_filter",
    "hash_file",
    "load_secret_rules",
    "looks_binary",
    "manifest_secret_scanner",
    "secret_option_values",
]
