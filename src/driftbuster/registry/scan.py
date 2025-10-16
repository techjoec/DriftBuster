from __future__ import annotations

import sys
import time
import re
from dataclasses import dataclass
from typing import Dict, Iterable, List, Optional, Sequence, Tuple


def is_windows() -> bool:
    return sys.platform.startswith("win32") or sys.platform.startswith("cygwin")


@dataclass(frozen=True)
class RegistryApp:
    display_name: str
    key_path: str
    hive: str  # "HKLM" or "HKCU"
    publisher: Optional[str] = None
    version: Optional[str] = None
    uninstall_string: Optional[str] = None
    install_location: Optional[str] = None
    view: str = "auto"  # "32" | "64" | "auto"


@dataclass(frozen=True)
class RegistryHit:
    path: str
    hive: str
    value_name: str
    data_preview: str
    reason: str


@dataclass(frozen=True)
class SearchSpec:
    keywords: Tuple[str, ...] = ()
    patterns: Tuple[re.Pattern[str], ...] = ()
    max_depth: int = 12
    max_hits: int = 200
    time_budget_s: float = 10.0


class _Backend:
    def enum_subkeys(self, hive: str, path: str, view: Optional[str]) -> List[str]:  # pragma: no cover - interface
        raise NotImplementedError

    def enum_values(self, hive: str, path: str, view: Optional[str]) -> List[Tuple[str, object]]:  # pragma: no cover - interface
        raise NotImplementedError


class _WinRegBackend(_Backend):
    def __init__(self) -> None:  # pragma: no cover - exercised in integration only
        import winreg  # type: ignore

        self._reg = winreg
        self._hives = {
            "HKLM": winreg.HKEY_LOCAL_MACHINE,
            "HKCU": winreg.HKEY_CURRENT_USER,
        }

    def _open(self, hive: str, path: str, view: Optional[str]):  # pragma: no cover - integration on Windows only
        reg = self._reg
        access = reg.KEY_READ
        if view == "64":
            access |= getattr(reg, "KEY_WOW64_64KEY", 0)
        elif view == "32":
            access |= getattr(reg, "KEY_WOW64_32KEY", 0)
        return reg.OpenKeyEx(self._hives[hive], path, 0, access)

    def enum_subkeys(self, hive: str, path: str, view: Optional[str]) -> List[str]:  # pragma: no cover - integration on Windows only
        reg = self._reg
        try:
            handle = self._open(hive, path, view)
        except OSError:
            return []
        results: List[str] = []
        try:
            index = 0
            while True:
                try:
                    name = reg.EnumKey(handle, index)
                except OSError:
                    break
                results.append(name)
                index += 1
        finally:
            reg.CloseKey(handle)
        return results

    def enum_values(self, hive: str, path: str, view: Optional[str]) -> List[Tuple[str, object]]:  # pragma: no cover - integration on Windows only
        reg = self._reg
        try:
            handle = self._open(hive, path, view)
        except OSError:
            return []
        results: List[Tuple[str, object]] = []
        try:
            index = 0
            while True:
                try:
                    name, data, _typ = reg.EnumValue(handle, index)
                except OSError:
                    break
                results.append((name, data))
                index += 1
        finally:
            reg.CloseKey(handle)
        return results


def _default_backend() -> _Backend:
    if not is_windows():
        raise RuntimeError("Windows Registry scanning requires Windows platform")
    return _WinRegBackend()


_UNINSTALL_PATH = r"Software\Microsoft\Windows\CurrentVersion\Uninstall"
_UNINSTALL_PATH_WOW64 = r"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall"


def enumerate_installed_apps(*, backend: Optional[_Backend] = None) -> Tuple[RegistryApp, ...]:
    """Enumerate installed applications via Uninstall registry keys.

    Returns:
        Ordered tuples of RegistryApp entries collected from HKLM/HKCU, both 64
        and 32-bit views when available.
    """

    if backend is None:
        backend = _default_backend()

    apps: List[RegistryApp] = []
    # Probe both hives and both views.
    probes = (
        ("HKLM", _UNINSTALL_PATH, "64"),
        ("HKLM", _UNINSTALL_PATH_WOW64, "32"),
        ("HKCU", _UNINSTALL_PATH, None),
    )
    for hive, base, view in probes:
        for subkey in backend.enum_subkeys(hive, base, view):
            key_path = f"{base}\\{subkey}"
            values = dict((name, data) for name, data in backend.enum_values(hive, key_path, view))
            display_name = str(values.get("DisplayName") or "").strip()
            if not display_name:
                continue
            app = RegistryApp(
                display_name=display_name,
                key_path=key_path,
                hive=hive,
                publisher=(str(values.get("Publisher")) if values.get("Publisher") else None),
                version=(str(values.get("DisplayVersion")) if values.get("DisplayVersion") else None),
                uninstall_string=(str(values.get("UninstallString")) if values.get("UninstallString") else None),
                install_location=(str(values.get("InstallLocation")) if values.get("InstallLocation") else None),
                view=view or "auto",
            )
            apps.append(app)
    # De-duplicate by (hive, key_path) keeping first seen entry
    seen: set[Tuple[str, str]] = set()
    unique: List[RegistryApp] = []
    for app in apps:
        k = (app.hive, app.key_path)
        if k in seen:
            continue
        seen.add(k)
        unique.append(app)
    # Sort by display_name for stable UX
    unique.sort(key=lambda a: (a.display_name.lower(), a.hive))
    return tuple(unique)


def _candidate_vendor_app_pairs(app_name: str) -> List[Tuple[str, str]]:
    parts = [p for p in re.split(r"[\s_-]+", app_name) if p]
    pairs: List[Tuple[str, str]] = []
    if len(parts) >= 2:
        # vendor app
        pairs.append((parts[0], " ".join(parts[1:])))
    # also consider full name as app under unknown vendor
    pairs.append(("", app_name))
    return pairs


def find_app_registry_roots(
    app_token: str,
    *,
    installed: Optional[Sequence[RegistryApp]] = None,
) -> Tuple[Tuple[str, str, Optional[str]], ...]:
    """Guess likely registry roots for a given app token.

    Returns:
        Tuples of (hive, path, view) suitable for searching.
    """

    token = app_token.strip().lower()
    candidates: List[Tuple[str, str, Optional[str]]] = []
    if installed:
        for app in installed:
            if token in app.display_name.lower() or (app.publisher and token in app.publisher.lower()):
                # Consider HKCU/HKLM software trees
                vendor_app = _candidate_vendor_app_pairs(app.display_name)
                for vendor, product in vendor_app:
                    vendor_seg = vendor.strip()
                    product_seg = product.strip()
                    common = [seg for seg in (vendor_seg, product_seg) if seg]
                    suffix = "\\".join(common) if common else product_seg or vendor_seg
                    if suffix:
                        candidates.extend(
                            [
                                ("HKCU", f"Software\\{suffix}", None),
                                ("HKLM", f"Software\\{suffix}", app.view if app.view in {"32", "64"} else None),
                                ("HKLM", f"Software\\Wow6432Node\\{suffix}", "32"),
                            ]
                        )
                # Add Uninstall key itself (useful for settings embedded there)
                candidates.append((app.hive, app.key_path, app.view if app.view in {"32", "64"} else None))

    # Always include broad vendor paths built from the token as a fallback
    base_suffix = app_token.strip()
    if base_suffix:
        candidates.extend(
            [
                ("HKCU", f"Software\\{base_suffix}", None),
                ("HKLM", f"Software\\{base_suffix}", None),
                ("HKLM", f"Software\\Wow6432Node\\{base_suffix}", "32"),
            ]
        )

    # Deduplicate and keep order
    seen_paths: set[Tuple[str, str, Optional[str]]] = set()
    ordered: List[Tuple[str, str, Optional[str]]] = []
    for item in candidates:
        if item in seen_paths:
            continue
        seen_paths.add(item)
        ordered.append(item)
    return tuple(ordered)


def search_registry(
    roots: Sequence[Tuple[str, str, Optional[str]]],
    spec: SearchSpec,
    *,
    backend: Optional[_Backend] = None,
) -> Tuple[RegistryHit, ...]:
    """Search registry trees under ``roots`` for values matching the spec.

    Traversal is breadth-first, respects ``max_depth``, stops at ``max_hits``,
    and halts when the time budget elapses. Nonexistent or inaccessible keys
    are skipped silently.
    """

    if backend is None:
        backend = _default_backend()

    keywords = tuple(k.lower() for k in spec.keywords)
    patterns = spec.patterns
    max_depth = max(0, int(spec.max_depth))
    max_hits = max(1, int(spec.max_hits))
    deadline = time.monotonic() + max(0.1, float(spec.time_budget_s))

    hits: List[RegistryHit] = []

    def _match_value(name: str, val: object) -> Optional[str]:
        text = None
        if isinstance(val, (str, bytes)):
            text = val.decode("utf-8", errors="replace") if isinstance(val, bytes) else val
        elif isinstance(val, (int, float)):
            text = str(val)
        elif isinstance(val, (list, tuple)):
            try:
                text = ", ".join(str(x) for x in val)
            except Exception:
                text = None
        if text is None:
            return None
        lower = text.lower()
        name_lower = name.lower()
        combined = f"{name_lower} {lower}"
        if keywords and not all(k in combined for k in keywords):
            return None
        if patterns and not (any(p.search(text) for p in patterns) or any(p.search(name) for p in patterns)):
            return None
        return text[:120]

    queue: List[Tuple[str, str, Optional[str], int]] = [(h, p, v, 0) for h, p, v in roots]
    seen: set[Tuple[str, str, Optional[str]]] = set()

    while queue and len(hits) < max_hits and time.monotonic() < deadline:
        hive, path, view, depth = queue.pop(0)
        key_id = (hive, path, view)
        if key_id in seen:
            continue
        seen.add(key_id)

        # Scan values at this key
        for name, data in backend.enum_values(hive, path, view):
            preview = _match_value(name, data)
            if preview is None:
                continue
            reason = "keyword/pattern match"
            hits.append(RegistryHit(path=path, hive=hive, value_name=name, data_preview=preview, reason=reason))
            if len(hits) >= max_hits:
                break
        if len(hits) >= max_hits:
            break

        # Enqueue subkeys if depth allows
        if depth >= max_depth:
            continue

        for child in backend.enum_subkeys(hive, path, view):
            child_path = f"{path}\\{child}"
            queue.append((hive, child_path, view, depth + 1))

    return tuple(hits)
