from __future__ import annotations

import re
from types import SimpleNamespace
from typing import Sequence

import builtins
import importlib
import sys

import pytest


def run_main(module, argv: Sequence[str]):
    return module.main(argv)


def test_registry_cli_requires_windows(monkeypatch: pytest.MonkeyPatch) -> None:
    from driftbuster import registry_cli as cli

    monkeypatch.setattr(cli, "is_windows", lambda: False)

    with pytest.raises(SystemExit) as exc:
        run_main(cli, ["list-apps"])
    assert "Windows" in str(exc.value)


def test_registry_cli_list_and_suggest(monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    from driftbuster import registry_cli as cli

    monkeypatch.setattr(cli, "is_windows", lambda: True)

    fake_apps = (
        SimpleNamespace(display_name="VendorA AppA", hive="HKLM", key_path=r"Software\\...\\Uninstall\\AppA", version="1.2.3", view="64"),
        SimpleNamespace(display_name="TinyTool", hive="HKCU", key_path=r"Software\\...\\Uninstall\\UserApp", version=None, view="auto"),
    )
    monkeypatch.setattr(cli, "enumerate_installed_apps", lambda: fake_apps)

    # list-apps prints both entries
    rc = run_main(cli, ["list-apps"])
    out = capsys.readouterr().out
    assert rc == 0
    assert "VendorA AppA" in out and "TinyTool" in out

    # suggest-roots uses find_app_registry_roots and prints hive \\ path
    called = {}

    def fake_roots(token: str, installed):
        called["token"] = token
        return (("HKLM", r"Software\\VendorA\\AppA", "64"),)

    monkeypatch.setattr(cli, "find_app_registry_roots", fake_roots)
    rc = run_main(cli, ["suggest-roots", "AppA"])
    out = capsys.readouterr().out
    assert rc == 0
    assert called["token"] == "AppA"
    normalized = out.replace("\\\\", "\\")
    assert "HKLM \\ Software\\VendorA\\AppA" in normalized


def test_registry_cli_search(monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    from driftbuster import registry_cli as cli

    monkeypatch.setattr(cli, "is_windows", lambda: True)

    monkeypatch.setattr(
        cli,
        "enumerate_installed_apps",
        lambda: (SimpleNamespace(display_name="VendorA AppA", hive="HKLM", key_path=r"Software\\...\\Uninstall\\AppA", view="64"),),
    )
    monkeypatch.setattr(
        cli,
        "find_app_registry_roots",
        lambda token, installed: (("HKLM", r"Software\\VendorA\\AppA", None),),
    )

    class Hit:
        def __init__(self):
            self.hive = "HKLM"
            self.path = r"Software\\VendorA\\AppA"
            self.value_name = "Server"
            self.data_preview = "api.internal.local"
            self.reason = "keyword/pattern match"

    monkeypatch.setattr(cli, "search_registry", lambda roots, spec: (Hit(),))

    rc = run_main(cli, [
        "search",
        "VendorA",
        "--keyword", "server",
        "--pattern", r"api\\.internal\\.local",
        "--max-depth", "3",
        "--max-hits", "5",
        "--time-budget", "1.0",
    ])
    out = capsys.readouterr().out
    assert rc == 0
    normalized = out.replace("\\\\", "\\")
    assert "HKLM \\ Software\\VendorA\\AppA :: Server = api.internal.local" in normalized
