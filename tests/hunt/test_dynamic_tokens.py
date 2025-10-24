from __future__ import annotations

from pathlib import Path

from driftbuster.hunt import (
    HuntRule,
    build_plan_transforms,
    default_rules,
    hunt_path,
    _extract_hits,
)


def test_plan_transforms_capture_regex_groups(tmp_path: Path) -> None:
    target = tmp_path / "settings.config"
    target.write_text(
        "connectionString=Server=db.internal.local;Database=Main;", encoding="utf-8"
    )

    rule = HuntRule(
        name="connection-string",
        description="Capture database host",
        token_name="database_server",
        patterns=(r"Server=([^;]+)",),
    )

    hits = _extract_hits(target.read_text(encoding="utf-8"), rule, target)
    transforms = build_plan_transforms(hits)

    assert len(transforms) == 1
    transform = transforms[0]
    assert transform.token_name == "database_server"
    assert transform.value == "db.internal.local"
    assert transform.placeholder == "{{ database_server }}"


def test_plan_transform_template_override(tmp_path: Path) -> None:
    target = tmp_path / "settings.config"
    target.write_text(
        "connectionString=Server=db.service;Database=Main;", encoding="utf-8"
    )

    rule = HuntRule(
        name="connection-string",
        description="Capture database host",
        token_name="database_server",
        patterns=(r"Server=([^;]+)",),
    )

    hits = _extract_hits(target.read_text(encoding="utf-8"), rule, target)
    transforms = build_plan_transforms(hits, placeholder_template="<<{token_name}>>")

    assert len(transforms) == 1
    assert transforms[0].placeholder == "<<database_server>>"


def test_hunt_json_includes_plan_transform_metadata(tmp_path: Path) -> None:
    target = tmp_path / "config.txt"
    target.write_text("Server host: app.local", encoding="utf-8")

    payload = hunt_path(target, rules=default_rules(), return_json=True)
    server_entry = next(
        entry for entry in payload if entry["rule"]["name"] == "server-name"
    )

    assert "metadata" in server_entry
    transform = server_entry["metadata"]["plan_transform"]
    assert transform["token_name"] == "server_name"
    assert transform["value"] == "app.local"
    assert transform["placeholder"] == "{{ server_name }}"
