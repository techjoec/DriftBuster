from __future__ import annotations

from pathlib import Path

import json

from driftbuster.hunt import PlanTransform
from driftbuster.token_approvals import (
    TokenApproval,
    TokenApprovalStore,
    collect_token_candidates,
)


def test_token_approval_store_roundtrip(tmp_path: Path) -> None:
    approvals = [
        TokenApproval(
            token_name="server_name",
            placeholder="{{ server_name }}",
            excerpt_hash="abc123",
            source_path="configs/app.config",
            catalog_variant="structured-settings-json",
            approved_by="alice",
            approved_at_utc="2025-01-01T00:00:00Z",
            secure_location="vault:prod/app",
        )
    ]
    store = TokenApprovalStore(approvals)
    path = tmp_path / "approvals.json"

    store.dump(path)

    payload = json.loads(path.read_text(encoding="utf-8"))
    assert payload == [
        {
            "token_name": "server_name",
            "placeholder": "{{ server_name }}",
            "excerpt_hash": "abc123",
            "source_path": "configs/app.config",
            "catalog_variant": "structured-settings-json",
            "approved_by": "alice",
            "approved_at_utc": "2025-01-01T00:00:00Z",
            "secure_location": "vault:prod/app",
        }
    ]

    loaded = TokenApprovalStore.load(path)
    assert loaded.entries() == tuple(approvals)

    updated = TokenApproval(
        token_name="server_name",
        placeholder="{{ server_name }}",
        excerpt_hash="abc123",
        source_path="configs/app.config",
        notes="Rotation scheduled",
    )
    loaded.add(updated)
    assert loaded.entries() == (updated,)


def test_collect_token_candidates_groups_results(tmp_path: Path) -> None:
    approval = TokenApproval(
        token_name="server_name",
        placeholder="{{ server_name }}",
        excerpt_hash="114f2f7b8991d81f43d458276cca670cc47ad74a03463f114b45070b6b69f028",
    )
    store = TokenApprovalStore([approval])

    hunts = [
        {
            "rule": {"name": "server-name", "token_name": "server_name"},
            "path": str(tmp_path / "app.config"),
            "relative_path": "app.config",
            "line_number": 12,
            "excerpt": "Server=prod-web-01.local",
            "metadata": {
                "plan_transform": {
                    "token_name": "server_name",
                    "value": "prod-web-01.local",
                    "placeholder": "{{ server_name }}",
                    "rule_name": "server-name",
                },
                "catalog_variant": "structured-settings-json",
                "sample_hash": "hash://sample",
            },
        },
        {
            "rule": {"name": "feature-flag", "token_name": "feature_flag"},
            "relative_path": "settings.json",
            "line_number": 5,
            "excerpt": "FeatureX=true",
            "metadata": {
                "plan_transform": {
                    "token_name": "feature_flag",
                    "value": "FeatureX=true",
                    "placeholder": "{{ feature_flag }}",
                    "rule_name": "feature-flag",
                }
            },
        },
    ]

    result = collect_token_candidates(hunts, approvals=store)

    assert len(result.approved) == 1
    assert len(result.pending) == 1

    approved_candidate = result.approved[0]
    assert approved_candidate.token_name == "server_name"
    assert approved_candidate.approval == approval
    assert approved_candidate.catalog_variant == "structured-settings-json"
    assert approved_candidate.sample_hash == "hash://sample"

    pending_candidate = result.pending[0]
    assert pending_candidate.token_name == "feature_flag"
    assert pending_candidate.approval is None

    duplicate = collect_token_candidates(hunts + hunts, approvals=store)
    assert len(duplicate.approved) == 1
    assert len(duplicate.pending) == 1

    transforms = [
        PlanTransform(
            token_name="certificate_thumbprint",
            value="abcdef",
            placeholder="{{ certificate_thumbprint }}",
            rule_name="certificate-thumbprint",
            path=tmp_path / "cert.txt",
            line_number=3,
            excerpt="abcdef",
        )
    ]
    transform_result = collect_token_candidates(transforms, approvals=TokenApprovalStore())
    assert len(transform_result.pending) == 1
    assert transform_result.pending[0].token_name == "certificate_thumbprint"
