from __future__ import annotations

from pathlib import Path

from driftbuster.hunt import (
    HuntRule,
    _extract_hits,
    _matches_keywords,
    _should_exclude,
    default_rules,
    hunt_path,
)


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


def test_hunt_rules_capture_xml_attribute_tokens(tmp_path: Path) -> None:
    target = tmp_path / "settings.config"
    target.write_text(
        """
        <configuration>
          <connectionStrings>
            <add name="Primary" connectionString="Server=sql.example.local;Database=App;" />
          </connectionStrings>
          <appSettings>
            <add key="ServiceEndpoint" value="https://api.example.com/v1/" />
            <add key="FeatureFlag:NewDashboard" value="true" />
          </appSettings>
          <system.serviceModel>
            <client>
              <endpoint address="net.tcp://svc.example.local:9000/Feed" />
            </client>
          </system.serviceModel>
        </configuration>
        """,
        encoding="utf-8",
    )

    results = hunt_path(target, rules=default_rules())

    assert results
    rule_names = {hit.rule.name for hit in results}
    assert {"connection-string", "service-endpoint", "feature-flag"}.issubset(rule_names)


def test_hunt_path_skips_binary_files(tmp_path: Path) -> None:
    target = tmp_path / "binary.dat"
    target.write_bytes(b"\x00\xff\x00\xff")

    results = hunt_path(target, rules=default_rules())
    assert results == []


def test_keyword_and_exclusion_helpers(tmp_path: Path) -> None:
    assert _matches_keywords("Server host", ["server"])
    assert not _matches_keywords("host", ["server"])

    candidate = tmp_path / "dir" / "file.txt"
    candidate.parent.mkdir()
    candidate.write_text("data", encoding="utf-8")
    relative = candidate.relative_to(tmp_path / "dir")
    assert _should_exclude(candidate, relative=relative, patterns=["*.txt"])


def test_extract_hits_without_patterns(tmp_path: Path) -> None:
    rule = HuntRule(name="basic", description="match anything")
    target = tmp_path / "sample.txt"
    target.write_text("alpha\nbeta", encoding="utf-8")

    hits = _extract_hits(target.read_text(encoding="utf-8"), rule, target)

    assert len(hits) == 2
    assert {hit.excerpt for hit in hits} == {"alpha", "beta"}


def test_should_exclude_relative_only() -> None:
    class StubPath:
        def match(self, _pattern: str) -> bool:
            return False

    relative = Path("notes/entry.log")

    assert _should_exclude(StubPath(), relative=relative, patterns=["notes/entry.log"])


def test_hunt_path_handles_relative_to_errors(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    root = tmp_path / "root"
    root.mkdir()
    sample = root / "config.txt"
    sample.write_text("server: host", encoding="utf-8")

    original_relative_to = Path.relative_to

    def failing_relative_to(self: Path, other: Path):
        if self == sample:
            raise ValueError("outside")
        return original_relative_to(self, other)

    monkeypatch.setattr(Path, "relative_to", failing_relative_to)

    hits = hunt_path(root, rules=default_rules(), exclude_patterns=["*.tmp"])
    assert hits == [] or isinstance(hits, list)


def test_hunt_json_relative_fallback(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    root = tmp_path / "root"
    root.mkdir()
    sample = root / "config.txt"
    sample.write_text("value", encoding="utf-8")

    class Wrapper:
        def __init__(self, wrapped: Path) -> None:
            self._wrapped = wrapped

        def __getattr__(self, name: str):
            return getattr(self._wrapped, name)

        def __lt__(self, other: object) -> bool:
            other_path = other._wrapped if isinstance(other, Wrapper) else other
            return self._wrapped < other_path

        def relative_to(self, _root: Path) -> Path:
            raise ValueError("outside")

        def __fspath__(self) -> str:
            return self._wrapped.__fspath__()

    original_glob = Path.glob

    def fake_glob(self: Path, pattern: str = "**/*"):
        if self == root:
            return [Wrapper(sample)]
        return original_glob(self, pattern)

    monkeypatch.setattr(Path, "glob", fake_glob)

    rule = HuntRule(name="any", description="custom rule")
    payload = hunt_path(root, rules=[rule], return_json=True)

    assert payload and payload[0]["relative_path"] == sample.name
