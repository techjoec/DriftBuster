from __future__ import annotations

from pathlib import Path

from driftbuster.hunt import default_rules, hunt_path


def test_hunt_path_returns_hits(tmp_path: Path) -> None:
    target = tmp_path / "config.txt"
    target.write_text("Server host: api.corp.local\nThumbprint: 0123456789abcdef0123456789abcdef01234567", encoding="utf-8")

    results = hunt_path(target, rules=default_rules())

    assert results
    rule_names = {hit.rule.name for hit in results}
    assert "server-name" in rule_names


def test_hunt_path_respects_exclusions(tmp_path: Path) -> None:
    directory = tmp_path / "configs"
    directory.mkdir()
    sample = directory / "info.txt"
    sample.write_text("Server host: db.prod.internal", encoding="utf-8")

    results = hunt_path(
        directory,
        rules=default_rules(),
        exclude_patterns=["*.txt"],
    )

    assert results == []


def test_hunt_path_json_format(tmp_path: Path) -> None:
    target = tmp_path / "config.txt"
    target.write_text("Server host: infra.corp.net", encoding="utf-8")

    payload = hunt_path(target, rules=default_rules(), return_json=True)

    assert isinstance(payload, list)
    assert payload
    entry = payload[0]
    assert entry["rule"]["name"] == "server-name"
    assert entry["relative_path"].endswith("config.txt")


def test_config_attribute_hunt_rules(tmp_path: Path) -> None:
    sample = tmp_path / "web.config"
    sample.write_text(
        """
        <configuration>
          <appSettings>
            <add key=\"ApiUrl\" value=\"https://api.example.com/\" />
          </appSettings>
          <connectionStrings>
            <add name=\"Default\" connectionString=\"Server=db01.internal;Database=main;\" />
          </connectionStrings>
        </configuration>
        """,
        encoding="utf-8",
    )

    results = hunt_path(sample, rules=default_rules())

    tokens = {hit.rule.token_name for hit in results if hit.rule.token_name}
    assert "app_setting_value" in tokens
    assert "connection_string" in tokens
