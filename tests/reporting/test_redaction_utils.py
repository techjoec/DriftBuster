from __future__ import annotations

from typing import Iterator

import pytest

from driftbuster.reporting.redaction import RedactionFilter, redact_data, resolve_redactor


def test_redaction_filter_applies_and_tracks_counts() -> None:
    redactor = RedactionFilter(tokens=("token", "tokenised", "secret"), placeholder="***")

    # Ensure tokens are ordered by length so the longest match wins first.
    assert redactor._ordered_tokens[0] == "tokenised"

    text = "tokenised value with token and secret"
    redacted = redactor.apply(text)

    assert redacted == "*** value with *** and ***"
    assert redactor.has_hits is True
    assert redactor.stats() == {"tokenised": 1, "token": 1, "secret": 1}

    redactor.reset()
    assert redactor.has_hits is False
    assert redactor.apply("") == ""


def test_redact_data_handles_nested_structures() -> None:
    redactor = RedactionFilter(tokens=("secret",))

    payload = {
        "message": "this is secret",
        "list": ["secret", {"nested": "no secret"}],
        "tuple": ("keep", "secret"),
        "set": {"secret", "visible"},
    }

    def generator() -> Iterator[str]:
        yield "value"
        yield "secret"

    payload["iterable"] = generator()

    redacted = redact_data(payload, redactor)

    assert redacted["message"].endswith("[REDACTED]")
    assert redacted["list"][0] == "[REDACTED]"
    assert redacted["list"][1]["nested"] == "no [REDACTED]"
    assert redacted["tuple"][1] == "[REDACTED]"
    assert "[REDACTED]" in redacted["set"]
    assert redacted["iterable"][1] == "[REDACTED]"


def test_resolve_redactor_variants() -> None:
    existing = RedactionFilter(tokens=("keep",))
    resolved = resolve_redactor(redactor=existing)
    assert resolved is existing

    auto = resolve_redactor(mask_tokens=("x", "y"), placeholder="?")
    assert isinstance(auto, RedactionFilter)
    assert auto.placeholder == "?"

    with pytest.raises(ValueError):
        resolve_redactor(redactor=existing, mask_tokens=("oops",))
