from driftbuster import secret_scanning


def test_load_secret_rules_reads_packaged_rules_and_caches() -> None:
    secret_scanning.reset_secret_rule_cache()

    rules, version, loaded = secret_scanning.load_secret_rules()
    assert loaded is True
    assert rules, "expected packaged secret rules to be available"
    assert version and version != "none"

    sentinel_rules = (object(),)
    secret_scanning._RULE_CACHE = sentinel_rules  # type: ignore[assignment]
    secret_scanning._RULE_VERSION = "cache-version"
    secret_scanning._RULE_LOADED = True
    cached_rules, cached_version, cached_loaded = secret_scanning.load_secret_rules()
    assert cached_rules is sentinel_rules
    assert cached_version == "cache-version"
    assert cached_loaded is True

    secret_scanning.reset_secret_rule_cache()
    assert secret_scanning._RULE_CACHE is None
    assert secret_scanning._RULE_VERSION is None
    assert secret_scanning._RULE_LOADED is None
