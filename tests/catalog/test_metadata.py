from driftbuster.catalog import DETECTION_CATALOG
from driftbuster.core.types import DetectionMatch, validate_detection_metadata


def test_catalog_injects_severity_hint_and_remediations() -> None:
    match = DetectionMatch(
        plugin_name="registry",
        format_name="registry-export",
        variant=None,
        confidence=0.9,
        reasons=["synthetic"],
    )

    metadata = validate_detection_metadata(match, DETECTION_CATALOG)

    assert metadata["catalog_severity"] == "high"
    assert metadata["catalog_severity_hint"].startswith(
        "Registry exports capture"
    )
    remediations = metadata["catalog_remediations"]
    assert isinstance(remediations, list)
    assert remediations
    references = metadata["catalog_references"]
    assert "docs/detection-types.md#registryexport" in references
    assert any(
        entry.get("id") == "registry-export-lockdown" and entry.get("category") == "secrets"
        for entry in remediations
    )


def test_variant_specific_severity_overrides_default() -> None:
    match = DetectionMatch(
        plugin_name="conf",
        format_name="unix-conf",
        variant="generic-directive-text",
        confidence=0.5,
        reasons=["synthetic"],
    )

    metadata = validate_detection_metadata(match, DETECTION_CATALOG)

    assert metadata["catalog_format"] == "unix-conf"
    assert metadata["catalog_variant"] == "generic-directive-text"
    assert metadata["catalog_severity"] == "medium"
    assert metadata["catalog_severity_hint"].startswith("Unix configuration files")
    catalog_remediations = metadata["catalog_remediations"]
    references = metadata["catalog_references"]
    assert "docs/detection-types.md#unixconf" in references
    assert any(entry.get("id") == "unix-conf-hardening" for entry in catalog_remediations)
