from __future__ import annotations

import re
from typing import Dict, List, Optional, Tuple

from driftbuster.registry.scan import (
    RegistryApp,
    RegistryHit,
    SearchSpec,
    enumerate_installed_apps,
    find_app_registry_roots,
    search_registry,
)


class FakeBackend:
    def __init__(self) -> None:
        # Structure: {(hive, path): {"subkeys": {name: {}}, "values": {name: value}}}
        self.nodes: Dict[Tuple[str, str], Dict[str, object]] = {}

    def add_key(self, hive: str, path: str, *, values: Optional[Dict[str, object]] = None) -> None:
        self.nodes.setdefault((hive, path), {"subkeys": {}, "values": {}})
        if values:
            self.nodes[(hive, path)]["values"].update(values)
        # ensure parent listings include this as a child
        if "\\" in path:
            parent = path.rsplit("\\", 1)[0]
            child = path.rsplit("\\", 1)[1]
            self.nodes.setdefault((hive, parent), {"subkeys": {}, "values": {}})
            self.nodes[(hive, parent)]["subkeys"][child] = True

    def enum_subkeys(self, hive: str, path: str, view: Optional[str]) -> List[str]:
        node = self.nodes.get((hive, path))
        if not node:
            return []
        return sorted(node["subkeys"].keys())  # type: ignore[index]

    def enum_values(self, hive: str, path: str, view: Optional[str]):
        node = self.nodes.get((hive, path))
        if not node:
            return []
        return list(node["values"].items())  # type: ignore[index]


def build_fake_registry() -> FakeBackend:
    fb = FakeBackend()
    # Uninstall keys
    base_hklm_64 = r"Software\Microsoft\Windows\CurrentVersion\Uninstall"
    base_hklm_32 = r"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    base_hkcu = r"Software\Microsoft\Windows\CurrentVersion\Uninstall"

    # App A (64-bit)
    fb.add_key("HKLM", base_hklm_64)
    fb.add_key("HKLM", base_hklm_64 + r"\AppA", values={
        "DisplayName": "VendorA AppA",
        "Publisher": "VendorA",
        "DisplayVersion": "1.2.3",
        "InstallLocation": r"C:\\Program Files\\VendorA\\AppA",
    })

    # App B (32-bit)
    fb.add_key("HKLM", base_hklm_32)
    fb.add_key("HKLM", base_hklm_32 + r"\AppB", values={
        "DisplayName": "VendorB AppB",
        "Publisher": "VendorB",
        "DisplayVersion": "4.5.6",
    })

    # User app (HKCU)
    fb.add_key("HKCU", base_hkcu)
    fb.add_key("HKCU", base_hkcu + r"\UserApp", values={
        "DisplayName": "TinyTool",
        "DisplayVersion": "0.9",
    })

    # Application settings trees
    fb.add_key("HKLM", r"Software\VendorA\AppA", values={"ConfigPath": r"C:\\data\\a.cfg"})
    fb.add_key("HKLM", r"Software\VendorA\AppA\Settings", values={"Server": "api.internal.local"})
    fb.add_key("HKLM", r"Software\Wow6432Node\VendorB\AppB", values={"Endpoint": "https://svc.corp.local"})
    fb.add_key("HKCU", r"Software\TinyTool", values={"Token": "abcd1234"})

    return fb


def test_enumerate_installed_apps_collects_from_multiple_hives():
    fb = build_fake_registry()
    apps = enumerate_installed_apps(backend=fb)
    names = [a.display_name for a in apps]
    assert "VendorA AppA" in names
    assert "VendorB AppB" in names
    assert "TinyTool" in names


def test_find_app_registry_roots_uses_installed_list():
    fb = build_fake_registry()
    apps = enumerate_installed_apps(backend=fb)
    roots = find_app_registry_roots("AppA", installed=apps)
    # Should include vendor/app software path and uninstall key
    assert any(r[0] == "HKLM" and r[1].startswith(r"Software\VendorA\AppA") for r in roots)
    assert any(r[1].startswith(r"Software\Microsoft\Windows\CurrentVersion\Uninstall") for r in roots)


def test_search_registry_matches_keywords_and_patterns():
    fb = build_fake_registry()
    roots = (
        ("HKLM", r"Software\VendorA\AppA", None),
        ("HKLM", r"Software\Wow6432Node\VendorB", "32"),
        ("HKCU", r"Software\TinyTool", None),
    )
    spec = SearchSpec(keywords=("server", "api"), patterns=(re.compile(r"api\.internal\.local"),))
    hits = search_registry(roots, spec, backend=fb)
    assert any(isinstance(h, RegistryHit) and h.value_name == "Server" for h in hits)

    # Pattern-only search should find https endpoints
    spec2 = SearchSpec(patterns=(re.compile(r"https://"),))
    hits2 = search_registry(roots, spec2, backend=fb)
    assert any(h.value_name == "Endpoint" for h in hits2)


def test_search_registry_depth_limit():
    fb = build_fake_registry()
    # Create deep nesting under a key to verify depth handling
    fb.add_key("HKLM", r"Software\VendorA\Deep")
    base = r"Software\VendorA\Deep"
    last = base
    for i in range(5):
        next_key = last + rf"\K{i}"
        fb.add_key("HKLM", next_key)
        last = next_key
    fb.add_key("HKLM", last, values={"Flag": "on"})

    roots = (("HKLM", base, None),)
    spec = SearchSpec(patterns=(re.compile(r"on"),), max_depth=2)
    hits = search_registry(roots, spec, backend=fb)
    # With depth=2, we should not reach the leaf at depth 5
    assert not hits
