"""Multi-server orchestration bridge for the GUI backend.

This module accepts a JSON payload describing the server scan plans,
executes detections locally via the Python engine, and emits structured
records that map to the .NET `ServerScanResponse` contract. It is invoked via
``python -m driftbuster.multi_server`` by :class:`DriftbusterBackend`.
"""

from __future__ import annotations

import json
import hashlib
import os
import shutil
import sys
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, Iterable, Mapping, MutableMapping, Sequence

from driftbuster.core.detector import Detector, DetectorIOError
from driftbuster.reporting.diff import build_unified_diff, canonicalise_text, canonicalise_xml
from driftbuster.hunt import default_rules, hunt_path

SCHEMA_VERSION = "multi-server.v1"
_SERVER_STATUS_QUEUED = "queued"
_SERVER_STATUS_RUNNING = "running"
_SERVER_STATUS_SUCCEEDED = "succeeded"
_SERVER_STATUS_FAILED = "failed"

_CONFIG_STATUS_FOUND = "found"
_CONFIG_STATUS_NOT_FOUND = "not_found"
_CONFIG_STATUS_PERMISSION_DENIED = "permission_denied"
_CONFIG_STATUS_OFFLINE = "offline"

_CANONICAL_NORMALISERS = {
    "xml": canonicalise_xml,
    "text": canonicalise_text,
}

DATA_ROOT_ENV = "DRIFTBUSTER_DATA_ROOT"


def _resolve_data_root() -> Path:
    override = os.environ.get(DATA_ROOT_ENV)
    if override:
        candidate = Path(os.path.expandvars(override)).expanduser()
        candidate.mkdir(parents=True, exist_ok=True)
        return candidate.resolve()

    if sys.platform.startswith("win"):
        for env_var in ("LOCALAPPDATA", "APPDATA"):
            configured = os.environ.get(env_var)
            if configured:
                candidate = Path(os.path.expandvars(configured)).expanduser() / "DriftBuster"
                candidate.mkdir(parents=True, exist_ok=True)
                return candidate.resolve()

        fallback = Path(os.path.expanduser("~")) / "AppData" / "Local" / "DriftBuster"
        fallback.mkdir(parents=True, exist_ok=True)
        return fallback.resolve()

    if sys.platform == "darwin":
        home = Path(os.path.expanduser("~"))
        candidate = home / "Library" / "Application Support" / "DriftBuster"
        candidate.mkdir(parents=True, exist_ok=True)
        return candidate.resolve()

    data_home = os.environ.get("XDG_DATA_HOME")
    if data_home:
        candidate = Path(os.path.expandvars(data_home)).expanduser() / "DriftBuster"
    else:
        candidate = Path(os.path.expanduser("~")) / ".local" / "share" / "DriftBuster"

    candidate.mkdir(parents=True, exist_ok=True)
    return candidate.resolve()


def _migrate_legacy_cache(destination: Path) -> None:
    legacy_root = Path("artifacts") / "cache" / "diffs"
    try:
        if not legacy_root.exists():
            return

        if any(destination.iterdir()):
            return

        destination.mkdir(parents=True, exist_ok=True)
        for source in legacy_root.glob("*"):
            if source.is_file():
                target = destination / source.name
                if not target.exists():
                    shutil.copy2(source, target)
    except Exception:
        # Migration is best-effort for developer setups.
        pass


def _resolve_cache_dir(cache_dir: str | Path | None) -> Path:
    if cache_dir:
        resolved = Path(cache_dir).expanduser()
        resolved.mkdir(parents=True, exist_ok=True)
        return resolved.resolve()

    destination = _resolve_data_root() / "cache" / "diffs"
    destination.mkdir(parents=True, exist_ok=True)
    _migrate_legacy_cache(destination)
    return destination


def _utc_timestamp() -> str:
    return datetime.now(timezone.utc).isoformat()


def _slugify(value: str) -> str:
    text = value.strip().lower()
    if not text:
        return ""
    return "".join(
        char if char.isalnum() or char in {"-", "_", "/"} else "-"
        for char in text
    )


def _determine_content_type(catalog_format: str | None) -> str:
    if catalog_format in {"structured-config-xml", "xml"}:
        return "xml"
    return "text"


def _canonicalise(content: str, content_type: str) -> str:
    normaliser = _CANONICAL_NORMALISERS.get(content_type, canonicalise_text)
    return normaliser(content)


def _read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8", errors="replace")


@dataclass(slots=True)
class BaselinePreference:
    is_preferred: bool = False
    priority: int = 0
    role: str = "auto"

    @classmethod
    def from_mapping(cls, payload: Mapping[str, object] | None) -> "BaselinePreference":
        if not payload:
            return cls()
        return cls(
            is_preferred=bool(payload.get("is_preferred", False)),
            priority=int(payload.get("priority", 0)),
            role=str(payload.get("role", "auto")) or "auto",
        )


@dataclass(slots=True)
class ExportOptions:
    include_catalog: bool = True
    include_drilldown: bool = True
    include_diffs: bool = True
    include_summary: bool = True

    @classmethod
    def from_mapping(cls, payload: Mapping[str, object] | None) -> "ExportOptions":
        if not payload:
            return cls()
        return cls(
            include_catalog=bool(payload.get("include_catalog", True)),
            include_drilldown=bool(payload.get("include_drilldown", True)),
            include_diffs=bool(payload.get("include_diffs", True)),
            include_summary=bool(payload.get("include_summary", True)),
        )


@dataclass(slots=True)
class Plan:
    host_id: str
    label: str
    scope: str
    roots: tuple[Path, ...]
    baseline: BaselinePreference = field(default_factory=BaselinePreference)
    export: ExportOptions = field(default_factory=ExportOptions)
    throttle_seconds: float | None = None
    cached_at: str | None = None

    @classmethod
    def from_mapping(cls, payload: Mapping[str, object]) -> "Plan":
        host_id = str(payload.get("host_id") or "").strip() or hashlib.sha1(os.urandom(16)).hexdigest()
        label = str(payload.get("label") or host_id).strip() or host_id
        scope = str(payload.get("scope") or "custom_roots").strip().lower()
        raw_roots = payload.get("roots") or []
        roots: list[Path] = []
        for entry in raw_roots:
            text = str(entry or "").strip()
            if not text:
                continue
            expanded = os.path.expanduser(os.path.expandvars(text))
            roots.append(Path(expanded))
        baseline = BaselinePreference.from_mapping(payload.get("baseline"))
        export = ExportOptions.from_mapping(payload.get("export"))
        throttle_value = payload.get("throttle_seconds")
        throttle = None
        if throttle_value is not None:
            try:
                throttle = float(throttle_value)
            except (TypeError, ValueError):
                throttle = None
        cached_at = payload.get("cached_at")
        cached_at_str = str(cached_at) if cached_at is not None else None
        return cls(
            host_id=host_id,
            label=label,
            scope=scope,
            roots=tuple(roots),
            baseline=baseline,
            export=export,
            throttle_seconds=throttle,
            cached_at=cached_at_str,
        )


@dataclass(slots=True)
class ConfigRecord:
    config_id: str
    display_name: str
    format_id: str
    content_type: str
    canonical: str
    raw: str
    metadata: Mapping[str, object]
    file_hash: str
    secrets: bool
    masked: bool
    source_path: str
    plugin_name: str
    relative_path: str


class DiffCache:
    def __init__(self, root: Path) -> None:
        self._root = Path(root)
        self._root.mkdir(parents=True, exist_ok=True)

    def _entry_path(self, host_id: str, config_id: str) -> Path:
        digest = hashlib.sha1(f"{host_id}:{config_id}".encode("utf-8")).hexdigest()
        return self._root / f"{digest}.json"

    def load(self, host_id: str, config_id: str, signature: str) -> Mapping[str, object] | None:
        path = self._entry_path(host_id, config_id)
        if not path.exists():
            return None
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            return None
        if payload.get("signature") != signature:
            return None
        return payload

    def save(self, host_id: str, config_id: str, signature: str, payload: Mapping[str, object]) -> None:
        path = self._entry_path(host_id, config_id)
        data = dict(payload)
        data["signature"] = signature
        path.write_text(json.dumps(data, ensure_ascii=False, sort_keys=True), encoding="utf-8")


class MultiServerRunner:
    def __init__(self, cache_dir: Path) -> None:
        self._detector = Detector()
        self._cache = DiffCache(Path(cache_dir))

    def run(self, plans: Sequence[Plan]) -> Mapping[str, object]:
        plans = list(plans)
        if not plans:
            return self._build_response([], {}, {}, {}, baseline_host_id="")

        baseline_plan = self._select_baseline(plans)
        baseline_host_id = baseline_plan.host_id
        host_results: list[Mapping[str, object]] = []
        host_configs: Dict[str, Dict[str, ConfigRecord]] = {}
        host_availability: Dict[str, str] = {}
        secrets_by_host: Dict[str, set[str]] = {}

        for plan in plans:
            emit_progress(plan.host_id, _SERVER_STATUS_RUNNING, f"Scanning {plan.label}")
            existing_roots = [root for root in plan.roots if root.exists()]
            if not existing_roots:
                message = "No accessible roots."
                host_results.append(self._result_payload(plan, status=_SERVER_STATUS_FAILED, availability=_CONFIG_STATUS_NOT_FOUND, message=message, roots=plan.roots))
                host_configs[plan.host_id] = {}
                host_availability[plan.host_id] = _CONFIG_STATUS_NOT_FOUND
                emit_progress(plan.host_id, _SERVER_STATUS_FAILED, message)
                continue

            try:
                secrets_by_host[plan.host_id] = self._collect_secret_hits(existing_roots)
                configs, used_cache = self._scan_plan(plan, existing_roots, secrets_by_host[plan.host_id])
                host_configs[plan.host_id] = configs
                host_availability[plan.host_id] = _CONFIG_STATUS_FOUND
                status_message = f"Evaluated {len(configs)} configuration(s)."
                host_results.append(self._result_payload(plan, status=_SERVER_STATUS_SUCCEEDED, availability=_CONFIG_STATUS_FOUND, message=status_message, roots=existing_roots, used_cache=used_cache))
                emit_progress(plan.host_id, _SERVER_STATUS_SUCCEEDED, status_message)
            except DetectorIOError as error:
                message = f"Permission denied: {error.path}"
                host_results.append(self._result_payload(plan, status=_SERVER_STATUS_FAILED, availability=_CONFIG_STATUS_PERMISSION_DENIED, message=message, roots=plan.roots))
                host_configs[plan.host_id] = {}
                host_availability[plan.host_id] = _CONFIG_STATUS_PERMISSION_DENIED
                emit_progress(plan.host_id, _SERVER_STATUS_FAILED, message)
            except Exception as exc:  # pragma: no cover - defensive fallback
                message = f"Scan failed: {exc}"[:160]
                host_results.append(self._result_payload(plan, status=_SERVER_STATUS_FAILED, availability=_CONFIG_STATUS_OFFLINE, message=message, roots=plan.roots))
                host_configs[plan.host_id] = {}
                host_availability[plan.host_id] = _CONFIG_STATUS_OFFLINE
                emit_progress(plan.host_id, _SERVER_STATUS_FAILED, message)

            if plan.throttle_seconds and plan.throttle_seconds > 0:
                time.sleep(plan.throttle_seconds)

        catalog, drilldown = self._build_catalog_and_drilldown(
            plans,
            host_configs,
            host_availability,
            baseline_host_id,
        )

        response = self._build_response(host_results, catalog, drilldown, host_availability, baseline_host_id)
        return response

    def _collect_secret_hits(self, roots: Sequence[Path]) -> set[str]:
        secret_paths: set[str] = set()
        rules = default_rules()
        for root in roots:
            try:
                hits = hunt_path(root, rules=rules, glob="**/*", return_json=True)
            except FileNotFoundError:
                continue
            for hit in hits:
                path = hit.get("path")
                if path:
                    secret_paths.add(os.path.abspath(path))
        return secret_paths

    def _scan_plan(
        self,
        plan: Plan,
        roots: Sequence[Path],
        secret_paths: set[str],
    ) -> tuple[Dict[str, ConfigRecord], bool]:
        configs: Dict[str, ConfigRecord] = {}
        cached_entries = 0
        total_entries = 0
        root_fingerprint = hashlib.sha1("|".join(sorted(str(root.resolve()) for root in roots)).encode("utf-8")).hexdigest()

        for root in roots:
            for path, match in self._detector.scan_path(root, glob="**/*"):
                if not path.is_file():
                    continue
                relative = path.relative_to(root)
                metadata = match.metadata or {}
                format_id = str(metadata.get("catalog_format") or match.format_name or "unknown")
                content_type = _determine_content_type(metadata.get("catalog_format"))
                config_id = self._normalise_config_id(match, metadata, relative)
                display_name = self._display_name(metadata, relative)
                raw_text = _read_text(path)
                file_hash = hashlib.sha256(raw_text.encode("utf-8")).hexdigest()
                signature = hashlib.sha256(f"{plan.host_id}:{config_id}:{root_fingerprint}:{file_hash}".encode("utf-8")).hexdigest()

                cached = self._cache.load(plan.host_id, config_id, signature)
                if cached:
                    canonical = str(cached.get("canonical") or "")
                    cached_entries += 1
                else:
                    canonical = _canonicalise(raw_text, content_type)
                    cache_payload = {
                        "canonical": canonical,
                        "content_type": content_type,
                        "metadata": metadata,
                        "file_hash": file_hash,
                    }
                    self._cache.save(plan.host_id, config_id, signature, cache_payload)

                total_entries += 1
                config_record = ConfigRecord(
                    config_id=config_id,
                    display_name=display_name,
                    format_id=format_id,
                    content_type=content_type,
                    canonical=canonical,
                    raw=raw_text,
                    metadata=metadata,
                    file_hash=file_hash,
                    secrets=os.path.abspath(path) in secret_paths,
                    masked=bool(metadata.get("has_masked_tokens", False)),
                    source_path=str(path),
                    plugin_name=match.plugin_name or "unknown",
                    relative_path=relative.as_posix(),
                )
                configs[config_id] = config_record

        used_cache = total_entries > 0 and cached_entries == total_entries
        return configs, used_cache

    def _display_name(self, metadata: Mapping[str, object], relative: Path) -> str:
        name = metadata.get("config_original_filename")
        if isinstance(name, str) and name.strip():
            return name.strip()
        return relative.as_posix()

    def _normalise_config_id(
        self,
        match,
        metadata: Mapping[str, object],
        relative: Path,
    ) -> str:
        format_id = str(metadata.get("catalog_format") or match.format_name or "config")
        variant = metadata.get("catalog_variant")
        candidates: list[str] = []

        for key in ("config_original_filename", "config_role", "msbuild_kind", "top_level_type"):
            value = metadata.get(key)
            if isinstance(value, str) and value.strip():
                candidates.append(value.strip())
        if relative.as_posix():
            candidates.append(relative.as_posix())

        for candidate in candidates:
            slug = _slugify(candidate)
            if slug:
                parts = [_slugify(format_id)]
                if isinstance(variant, str) and variant.strip():
                    parts.append(_slugify(variant))
                parts.append(slug)
                return "/".join(part for part in parts if part)

        fallback = relative.as_posix() or match.plugin_name or "config"
        digest = hashlib.sha1(fallback.encode("utf-8", "ignore")).hexdigest()[:12]
        return f"{_slugify(format_id)}#{digest}"

    def _select_baseline(self, plans: Sequence[Plan]) -> Plan:
        candidates = sorted(
            plans,
            key=lambda plan: (
                not plan.baseline.is_preferred,
                -plan.baseline.priority,
                plans.index(plan),
            ),
        )
        return candidates[0]

    def _result_payload(
        self,
        plan: Plan,
        *,
        status: str,
        availability: str,
        message: str,
        roots: Sequence[Path],
        used_cache: bool = False,
    ) -> Mapping[str, object]:
        return {
            "host_id": plan.host_id,
            "label": plan.label,
            "status": status,
            "message": message,
            "timestamp": _utc_timestamp(),
            "roots": [str(root) for root in roots],
            "used_cache": used_cache,
            "availability": availability,
        }

    def _build_catalog_and_drilldown(
        self,
        plans: Sequence[Plan],
        host_configs: Mapping[str, Mapping[str, ConfigRecord]],
        host_availability: Mapping[str, str],
        baseline_host_id: str,
    ) -> tuple[list[Mapping[str, object]], list[Mapping[str, object]]]:
        config_index: Dict[str, Dict[str, ConfigRecord]] = {}
        for host_id, configs in host_configs.items():
            for config_id, record in configs.items():
                config_index.setdefault(config_id, {})[host_id] = record

        catalog_entries: list[Mapping[str, object]] = []
        drilldown_entries: list[Mapping[str, object]] = []
        hosts_by_id = {plan.host_id: plan for plan in plans}
        total_hosts = len(plans)

        for config_id in sorted(config_index.keys()):
            per_host = config_index[config_id]
            baseline_record = per_host.get(baseline_host_id)
            if baseline_record is None and per_host:
                # Fallback baseline when preferred host missing the config
                fallback_host_id = sorted(per_host.keys())[0]
                baseline_record = per_host[fallback_host_id]
            
            format_id = baseline_record.format_id if baseline_record else next(iter(per_host.values())).format_id
            content_type = baseline_record.content_type if baseline_record else "text"
            present_host_ids = [host_id for host_id in plans_order(plans) if per_host.get(host_id)]
            present_labels = [hosts_by_id[host_id].label for host_id in present_host_ids]
            missing_labels = [hosts_by_id[host_id].label for host_id in plans_order(plans) if host_id not in per_host]

            drift_stats: Dict[str, int] = {}
            unified_diffs: Dict[str, Mapping[str, object]] = {}
            baseline_content = baseline_record.canonical if baseline_record else ""
            baseline_raw = baseline_record.raw if baseline_record else ""

            for host_id in plans_order(plans):
                record = per_host.get(host_id)
                if not record:
                    continue
                diff = build_unified_diff(
                    before=baseline_content,
                    after=record.canonical,
                    content_type=content_type,
                    from_label=f"{hosts_by_id.get(baseline_host_id, hosts_by_id[host_id]).label}:{config_id}",
                    to_label=f"{hosts_by_id[host_id].label}:{config_id}",
                )
                stats = diff.stats
                drift_lines = int(stats.get("added_lines", 0)) + int(stats.get("removed_lines", 0)) + int(stats.get("changed_lines", 0))
                drift_stats[host_id] = drift_lines
                unified_diffs[host_id] = {
                    "before": baseline_raw,
                    "after": record.raw,
                    "diff": diff.diff,
                }

            drift_count = sum(1 for value in drift_stats.values() if value > 0)
            severity = "none"
            if drift_count >= max(1, total_hosts // 2):
                severity = "high"
            elif drift_count > 0:
                severity = "medium"

            coverage_status = "full"
            if not present_host_ids:
                coverage_status = "missing"
            elif len(present_host_ids) != total_hosts:
                coverage_status = "partial"

            has_secrets = any(per_host[host_id].secrets for host_id in per_host)
            has_masked = any(per_host[host_id].masked for host_id in per_host)
            has_validation = len(missing_labels) > 0

            catalog_entries.append({
                "config_id": config_id,
                "display_name": baseline_record.display_name if baseline_record else config_id,
                "format": format_id,
                "drift_count": drift_count,
                "severity": severity,
                "present_hosts": present_labels,
                "missing_hosts": missing_labels,
                "last_updated": _utc_timestamp(),
                "has_secrets": has_secrets,
                "has_masked_tokens": has_masked,
                "has_validation_issues": has_validation,
                "coverage_status": coverage_status,
            })

            servers: list[Mapping[str, object]] = []
            chosen_diff_host = baseline_host_id if baseline_host_id in unified_diffs else (present_host_ids[0] if present_host_ids else baseline_host_id)
            for host_id in plans_order(plans):
                plan = hosts_by_id[host_id]
                record = per_host.get(host_id)
                availability = host_availability.get(host_id, _CONFIG_STATUS_NOT_FOUND)
                present = record is not None and availability == _CONFIG_STATUS_FOUND
                drift_lines = drift_stats.get(host_id, 0) if present else 0
                secrets = record.secrets if record else False
                masked = record.masked if record else False
                status_text = "Missing"
                if present:
                    status_text = "Drift" if drift_lines > 0 else "Match"
                elif availability == _CONFIG_STATUS_PERMISSION_DENIED:
                    status_text = "Permission denied"
                elif availability == _CONFIG_STATUS_OFFLINE:
                    status_text = "Offline"

                servers.append({
                    "host_id": host_id,
                    "label": plan.label,
                    "present": present,
                    "is_baseline": host_id == baseline_host_id,
                    "status": status_text,
                    "drift_lines": drift_lines,
                    "has_secrets": secrets,
                    "masked": masked,
                    "redaction_status": "Masked" if masked else ("Secrets" if secrets else "Visible"),
                    "last_seen": _utc_timestamp(),
                    "presence_status": _CONFIG_STATUS_FOUND if present else availability,
                })

            chosen = unified_diffs.get(chosen_diff_host)
            if chosen is None and unified_diffs:
                chosen = next(iter(unified_diffs.values()))
            else:
                chosen = chosen or {"before": baseline_raw, "after": baseline_raw, "diff": ""}

            drilldown_entries.append({
                "config_id": config_id,
                "display_name": baseline_record.display_name if baseline_record else config_id,
                "format": format_id,
                "servers": servers,
                "baseline_host_id": baseline_host_id,
                "diff_before": chosen.get("before", ""),
                "diff_after": chosen.get("after", ""),
                "unified_diff": chosen.get("diff", ""),
                "has_secrets": has_secrets,
                "has_masked_tokens": has_masked,
                "has_validation_issues": has_validation,
                "notes": [f"Detected via {baseline_record.plugin_name if baseline_record else 'detector'}"],
                "provenance": f"detector:{baseline_record.plugin_name if baseline_record else 'unknown'}",
                "drift_count": drift_count,
                "last_updated": _utc_timestamp(),
            })

        return catalog_entries, drilldown_entries

    def _build_response(
        self,
        host_results: Sequence[Mapping[str, object]],
        catalog: Sequence[Mapping[str, object]],
        drilldown: Sequence[Mapping[str, object]],
        availability: Mapping[str, str],
        baseline_host_id: str,
    ) -> Mapping[str, object]:
        summary = {
            "baseline_host_id": baseline_host_id,
            "total_hosts": len(host_results),
            "configs_evaluated": len(catalog),
            "drifting_configs": sum(1 for entry in catalog if entry.get("drift_count", 0)),
            "generated_at": _utc_timestamp(),
        }

        return {
            "version": SCHEMA_VERSION,
            "results": host_results,
            "catalog": catalog,
            "drilldown": drilldown,
            "summary": summary,
        }


def plans_order(plans: Sequence[Plan]) -> list[str]:
    return [plan.host_id for plan in plans]


def emit_progress(host_id: str, status: str, message: str) -> None:
    payload = {
        "type": "progress",
        "payload": {
            "host_id": host_id,
            "status": status,
            "message": message,
            "timestamp": _utc_timestamp(),
        },
    }
    print(json.dumps(payload, ensure_ascii=False))
    sys.stdout.flush()


def _load_request() -> Mapping[str, object]:
    raw = sys.stdin.read()
    try:
        payload = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise SystemExit(f"Invalid JSON payload: {exc}")
    return payload


def _build_plans(request: Mapping[str, object]) -> list[Plan]:
    plans_payload = request.get("plans") or []
    if not isinstance(plans_payload, Sequence):
        raise SystemExit("'plans' must be an array")
    plans: list[Plan] = []
    for entry in plans_payload:
        if not isinstance(entry, Mapping):
            continue
        plans.append(Plan.from_mapping(entry))
    return plans


def main() -> int:
    try:
        request = _load_request()
        schema_version = str(request.get("schema_version") or SCHEMA_VERSION)
        if schema_version != SCHEMA_VERSION:
            raise SystemExit(f"Unsupported schema version: {schema_version}")
        cache_dir = _resolve_cache_dir(request.get("cache_dir"))
        plans = _build_plans(request)
        runner = MultiServerRunner(cache_dir)
        response = runner.run(plans)
        print(json.dumps({"type": "result", "payload": response}, ensure_ascii=False))
        sys.stdout.flush()
        return 0
    except SystemExit as exc:
        message = str(exc) or "Request aborted"
        print(json.dumps({"type": "error", "message": message}, ensure_ascii=False))
        sys.stdout.flush()
        return 1
    except Exception as exc:  # pragma: no cover - defensive fallback
        message = f"Unhandled error: {exc}"
        print(json.dumps({"type": "error", "message": message}, ensure_ascii=False))
        sys.stdout.flush()
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
