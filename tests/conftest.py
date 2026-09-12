from __future__ import annotations

from collections.abc import Iterator

import pytest


@pytest.fixture(autouse=True)
def _isolate_secret_rule_caches() -> Iterator[None]:
    """Snapshot and restore the module-level secret rule caches around every test."""

    from driftbuster import secret_scanning

    saved = (secret_scanning._RULE_CACHE, secret_scanning._RULE_VERSION, secret_scanning._RULE_LOADED)
    try:
        yield
    finally:
        secret_scanning._RULE_CACHE, secret_scanning._RULE_VERSION, secret_scanning._RULE_LOADED = saved
