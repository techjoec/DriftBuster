from __future__ import annotations

import codecs
from pathlib import Path
from textwrap import dedent

import pytest

from driftbuster.core.types import DetectionMatch
from driftbuster.formats.format_registry import decode_text
from driftbuster.formats.ini import IniPlugin
import driftbuster.formats.ini.plugin as ini_plugin_module


def _detect(
    plugin: IniPlugin,
    filename: str,
    content: str,
    *,
    raw: bytes | None = None,
) -> DetectionMatch | None:
    path = Path(filename)
    if raw is None:
        data = content.encode("utf-8")
        text = content
    else:
        data = raw
        text, _encoding = decode_text(raw)
    return plugin.detect(path, data, text)


@pytest.fixture()
def ini_plugin() -> IniPlugin:
    return IniPlugin()


@pytest.fixture()
def dotenv_export_sample() -> str:
    return dedent(
        """
        DATABASE_URL=postgres://localhost/app
        export LOG_LEVEL=info
        FEATURE_FLAG=1
        """
    ).strip()


@pytest.fixture()
def java_properties_colon_sample() -> str:
    return dedent(
        """
        spring.datasource.url: jdbc:postgresql://localhost/example
        management.endpoints.enabled=true
        multiline.value = first line \\
         second line
        """
    ).strip()


@pytest.fixture()
def directive_conf_sample() -> str:
    return dedent(
        """
        Include conf.d/*.conf
        Option ForceCommand internal-sftp
        Alias /static/ "/var/www/static"
        """
    ).strip()


@pytest.fixture()
def hybrid_ini_json_sample() -> str:
    return dedent(
        '''
        [general]
        enabled=true
        {
            "extra": true
        }
        '''
    ).strip()


@pytest.fixture()
def mixed_newline_sample() -> str:
    return "[mix]\r\nfirst=1\nsecond=2\r\nthird=3\n"


@pytest.fixture()
def latin1_credentials_sample() -> tuple[str, bytes]:
    text = dedent(
        """
        [credenciales]
        contraseña=secreta
        api_key=abcd1234
        ; nota=ñ
        """
    ).strip()
    return text, text.encode("latin-1")


def test_ini_plugin_detects_sections_and_keys(ini_plugin: IniPlugin) -> None:
    match = _detect(
        ini_plugin,
        "settings.ini",
        dedent(
            """
            [general]
            enabled = true
            threshold = 10
            [logging]
            level = info
            ; trailing comment
            """
        ).strip(),
    )

    assert match is not None
    assert match.format_name == "ini"
    assert match.variant == "sectioned-ini"
    assert match.metadata is not None
    assert match.metadata["section_count"] == 2
    assert match.metadata["key_value_pairs"] >= 3
    assert match.metadata["encoding_info"]["codec"] == "utf-8"
    assert match.metadata["encoding"] == "utf-8"
    comment_style = match.metadata["comment_style"]
    assert comment_style["markers"] == [";"]
    assert comment_style["supports_inline_comments"] is False
    assert comment_style["uses_export_prefix"] is False
    assert any("Section headers" in reason for reason in match.reasons)


def test_ini_plugin_detects_desktop_ini_variant(ini_plugin: IniPlugin) -> None:
    match = _detect(
        ini_plugin,
        "desktop.ini",
        """
        [.ShellClassInfo]
        IconResource=shell32.dll,3
        ConfirmFileOp=0
        """.strip(),
    )

    assert match is not None
    assert match.variant == "desktop-ini"
    assert match.metadata is not None
    assert ".ShellClassInfo" in match.metadata.get("sections", [])


def test_ini_plugin_classifies_env_files(
    ini_plugin: IniPlugin, dotenv_export_sample: str
) -> None:
    match = _detect(
        ini_plugin,
        ".env",
        dotenv_export_sample,
    )

    assert match is not None
    assert match.format_name == "env-file"
    assert match.variant == "dotenv"
    assert match.metadata is not None
    comment_style = match.metadata["comment_style"]
    assert comment_style["uses_export_prefix"] is True
    assert match.metadata.get("export_assignments") == 1
    assert any("dotenv" in reason.lower() for reason in match.reasons)


def test_ini_plugin_preserves_java_properties_classification(
    ini_plugin: IniPlugin,
    java_properties_colon_sample: str,
) -> None:
    match = _detect(
        ini_plugin,
        "application.properties",
        java_properties_colon_sample,
    )

    assert match is not None
    assert match.format_name == "ini"
    assert match.variant == "java-properties"
    assert any("java properties" in reason.lower() for reason in match.reasons)
    assert match.metadata is not None
    assert match.metadata.get("colon_separator_pairs") == 1
    assert match.metadata.get("continuations") == 1


def test_ini_plugin_classifies_unix_conf_variants(ini_plugin: IniPlugin) -> None:
    match = _detect(
        ini_plugin,
        "httpd.conf",
        """
        LoadModule authz_core_module modules/mod_authz_core.so
        ServerName example.com
        <Directory "/var/www/html">
            AllowOverride None
        </Directory>
        """.strip(),
    )

    assert match is not None
    assert match.format_name == "unix-conf"
    assert match.variant == "apache-conf"
    assert any("apache" in reason.lower() for reason in match.reasons)


def test_ini_plugin_classifies_sectionless_ini_variant(ini_plugin: IniPlugin) -> None:
    match = _detect(
        ini_plugin,
        "plain.ini",
        dedent(
            """
            setting: true
            path: /etc/example
            """
        ).strip(),
    )

    assert match is not None
    assert match.format_name == "ini"
    assert match.variant == "sectionless-ini"
    assert match.metadata is not None
    assert match.metadata.get("colon_separator_pairs") == 2
    assert any("sectionless" in reason.lower() for reason in match.reasons)


def test_ini_plugin_detects_ini_json_hybrids(
    ini_plugin: IniPlugin, hybrid_ini_json_sample: str
) -> None:
    match = _detect(
        ini_plugin,
        "hybrid.conf",
        hybrid_ini_json_sample,
    )

    assert match is not None
    assert match.format_name == "ini-json-hybrid"
    assert match.variant == "section-json-hybrid"
    assert any("hybrid" in reason.lower() for reason in match.reasons)


def test_ini_plugin_detects_inline_closing_json_hybrid(
    ini_plugin: IniPlugin,
) -> None:
    match = _detect(
        ini_plugin,
        "hybrid_inline.conf",
        dedent(
            """
            [service]
            data = {
                "foo": "bar",
                "baz": "qux"}
            """
        ).strip(),
    )

    assert match is not None
    assert match.format_name == "ini-json-hybrid"
    assert match.variant == "section-json-hybrid"
    assert any("hybrid" in reason.lower() for reason in match.reasons)


def test_ini_plugin_does_not_flag_placeholder_braces_as_hybrid(
    ini_plugin: IniPlugin,
) -> None:
    match = _detect(
        ini_plugin,
        "placeholders.ini",
        dedent(
            """
            [template]
            pattern={value}
            include={% include %}
            path={HOME}/bin
            """
        ).strip(),
    )

    assert match is not None
    assert match.format_name == "ini"
    assert match.variant == "sectioned-ini"
    assert all("hybrid" not in reason.lower() for reason in match.reasons)


def test_ini_plugin_classifies_directive_conf_variant(
    ini_plugin: IniPlugin, directive_conf_sample: str
) -> None:
    match = _detect(
        ini_plugin,
        "ssh_config.conf",
        directive_conf_sample,
    )

    assert match is not None
    assert match.format_name == "unix-conf"
    assert match.variant == "directive-conf"
    assert match.metadata is not None
    assert match.metadata.get("directive_line_count") == 3
    assert any("directive" in reason.lower() for reason in match.reasons)


def test_ini_plugin_classifies_nginx_conf_variant(ini_plugin: IniPlugin) -> None:
    match = _detect(
        ini_plugin,
        "nginx.conf",
        dedent(
            """
            include /etc/nginx/mime.types;
            server {
                listen 80;
                location / {
                    proxy_pass http://app;
                }
            }
            """
        ).strip(),
    )

    assert match is not None
    assert match.format_name == "unix-conf"
    assert match.variant == "nginx-conf"
    assert match.metadata is not None
    assert match.metadata.get("directive_line_count") == 1
    assert any("nginx" in reason.lower() for reason in match.reasons)


def test_ini_plugin_records_bom_and_sensitive_hints(ini_plugin: IniPlugin) -> None:
    content = """
    [credentials]
    db_password = hunter2 # rotate soon
    api_token=deadbeef ; inline note
    plain_key = value
    """.strip()
    raw = codecs.BOM_UTF8 + content.encode("utf-8")

    match = _detect(ini_plugin, "secrets.ini", content, raw=raw)

    assert match is not None
    assert match.metadata is not None
    encoding_info = match.metadata["encoding_info"]
    assert encoding_info["bom_present"] is True
    assert encoding_info["codec"] == "utf-8-sig"
    comment_style = match.metadata["comment_style"]
    assert set(comment_style["markers"]) == {"#", ";"}
    assert comment_style["supports_inline_comments"] is True
    sensitive_hints = match.metadata.get("sensitive_key_hints", [])
    hint_pairs = {(hint["key"], hint["keyword"]) for hint in sensitive_hints}
    assert ("db_password", "password") in hint_pairs
    assert ("api_token", "token") in hint_pairs
    assert any("Sensitive key" in reason for reason in match.reasons)
    assert match.metadata.get("remediations") == [
        "Rotate credentials referenced in sensitive_key_hints.",
    ]
    assert any("utf-8-sig" in reason for reason in match.reasons)


def test_ini_plugin_handles_mixed_newlines(
    ini_plugin: IniPlugin, mixed_newline_sample: str
) -> None:
    match = _detect(
        ini_plugin,
        "mixed.ini",
        mixed_newline_sample,
    )

    assert match is not None
    assert match.format_name == "ini"
    assert match.metadata is not None
    assert match.metadata.get("key_value_pairs") == 3
    assert match.metadata.get("key_density")


def test_ini_plugin_reports_latin1_encoding(
    ini_plugin: IniPlugin, latin1_credentials_sample: tuple[str, bytes]
) -> None:
    text, raw = latin1_credentials_sample
    match = _detect(
        ini_plugin,
        "credenciales.ini",
        text,
        raw=raw,
    )

    assert match is not None
    assert match.metadata is not None
    encoding_info = match.metadata["encoding_info"]
    assert encoding_info["codec"] == "latin-1"
    assert encoding_info["bom_present"] is False
    assert any("latin-1" in reason for reason in match.reasons)


def test_ini_plugin_rejects_plain_text(ini_plugin: IniPlugin) -> None:
    match = _detect(
        ini_plugin,
        "notes.txt",
        "This is just a paragraph of text without configuration cues.",
    )

    assert match is None


def test_ini_plugin_rejects_yaml_with_colons(ini_plugin: IniPlugin) -> None:
    match = _detect(
        ini_plugin,
        "config.yaml",
        """
        foo: bar
        bar: baz
        """.strip(),
    )

    assert match is None


def test_ini_plugin_returns_none_without_text(ini_plugin: IniPlugin) -> None:
    assert ini_plugin.detect(Path("empty.ini"), b"", None) is None


def test_ini_plugin_handles_duplicates_and_sensitive_keys(ini_plugin: IniPlugin) -> None:
    content = dedent(
        """
        [General]
        password = hunter2
        client-secret = value1
        client-secret = another
        [general]
        empty=
        ;   
        key=
        inline = value ; comment
        export FLAG=true
        """
    ).strip()

    match = _detect(ini_plugin, "config.ini", content)

    assert match is not None
    assert match.metadata is not None
    assert match.metadata["section_count"] == 1
    hints = match.metadata.get("sensitive_key_hints", [])
    assert any(hint["key"].lower() == "client-secret" for hint in hints)
    comment_style = match.metadata["comment_style"]
    assert comment_style["supports_inline_comments"] is True


def test_ini_plugin_extension_hint_without_registered_extension(
    ini_plugin: IniPlugin, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(ini_plugin_module, "_INI_EXTENSIONS", {".cfg", ".conf"})
    match = _detect(ini_plugin, "settings.ini", "key=value")
    assert match is not None
    assert any("suffix .ini" in reason for reason in match.reasons)


def test_ini_plugin_returns_none_when_structure_weak(ini_plugin: IniPlugin) -> None:
    content = "\n".join(["foo: bar", "alpha", "beta", "gamma", "delta"])
    assert _detect(ini_plugin, "notes.txt", content) is None


def test_ini_plugin_requires_multiple_signals(ini_plugin: IniPlugin) -> None:
    content = "Include conf.d/*.conf\n"
    assert _detect(ini_plugin, "rules.txt", content) is None


def test_ini_plugin_confidence_boost_for_many_pairs(ini_plugin: IniPlugin) -> None:
    lines = [f"key{i}=value{i}" for i in range(6)]
    match = _detect(ini_plugin, "bulk.ini", "\n".join(lines))
    assert match is not None
    assert match.confidence >= 0.7


def test_ini_plugin_detects_block_json_assignments(ini_plugin: IniPlugin) -> None:
    content = dedent(
        """
        foo = value {
        bar = 1
        also = { "inner": true }
        }
        [Next]
        setting=true
        json = { "key": "value" }
        """
    ).strip()

    match = _detect(ini_plugin, "hybrid.ini", content)
    assert match is not None
    assert match.metadata is not None
    assert "section_count" in match.metadata


def test_ini_plugin_breaks_block_json_on_section_header(ini_plugin: IniPlugin) -> None:
    # JSON block starts, then a new section header appears before a closing brace.
    content = dedent(
        """
        options = {
        "a": 1
        [Next]
        afterwards=true
        """
    ).strip()

    match = _detect(ini_plugin, "section-before-close.ini", content)
    assert match is not None
    # Should still be classified as INI due to key/value pairs and signals
    assert match.format_name in {"ini", "ini-json-hybrid"}


def test_ini_plugin_breaks_block_json_on_nested_assignment(ini_plugin: IniPlugin) -> None:
    # A nested assignment with `{` and `=` appears before the original JSON block closes.
    content = dedent(
        """
        data = {
        "a": 1
        nested = {
            "b": 2
        }
        key=value
        """
    ).strip()

    match = _detect(ini_plugin, "nested-json-before-close.ini", content)
    assert match is not None
    # Depending on signal balance, this may classify as INI or env-file.
    assert match.format_name in {"ini", "ini-json-hybrid", "env-file"}


def test_ini_plugin_inline_comment_loop_skips_empty_values(ini_plugin: IniPlugin) -> None:
    # Ensure a key with an empty value is present to exercise the continue branch.
    content = dedent(
        """
        empty=
        key=value ; comment
        """
    ).strip()

    match = _detect(ini_plugin, "empty-value.ini", content)
    assert match is not None
    assert match.metadata is not None
    # Confirm inline comment detection still works overall
    style = match.metadata["comment_style"]
    assert style["supports_inline_comments"] is True


def test_ini_plugin_handles_blank_comments_and_inline_markers(ini_plugin: IniPlugin) -> None:
    content = dedent(
        """
        # comment
           
        key = value ; inline # extra
        """
    ).strip()

    match = _detect(ini_plugin, "markers.ini", content)
    assert match is not None
    assert match.metadata is not None
    comment_style = match.metadata["comment_style"]
    assert set(comment_style["markers"]) == {";", "#"}
    assert comment_style["supports_inline_comments"] is True
