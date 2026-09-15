"""profile-store normaliser (recorded divergence, "Detection profiles"): the typed port stores ``str(value)`` for a detection profile text
field that Python keeps as a JSON number, bool, list or dict. For each record pair, a Python value in one of those fields that is not a str
or None is replaced by the port's value at the same place when that value is exactly ``str(value)`` (a mapping's keys in the insertion
order the record's ``key_order`` gives), and that field's ``key_order`` child becomes None, a str's; every other byte is compared as is.
Only the record positions that hold a text field are normalised (``RECORD``): a profile's ``description`` in ``summary``, ``to_dict`` and
``round_trip``, a config's text fields in ``to_dict`` and ``round_trip``, and ``expected_format`` / ``expected_variant`` of the matches in
``matching_configs`` and ``find_config``. A key of the same name anywhere else (inside user ``metadata``, say) is compared as is.

Usage: profile_text_fields.py <python.jsonl> <port.jsonl>  -- prints the Python records (a record re-serialised only when a value was
replaced) and, on stderr, the number of records changed.
       profile_text_fields.py --self-test  -- proves the normaliser replaces a text field at its position and leaves a stringified value
under a text field's name inside metadata (or any other position) to fail the compare; exits non-zero otherwise.
"""

from __future__ import annotations

import json
import sys

TEXT = "text"


class Items:
    """Schema of a list whose every item follows ``item``."""

    def __init__(self, item: object) -> None:
        self.item = item


# Where the record holds a detection profile text field (TEXT); every other position is compared as is.
APPLIED = {"expected_format": TEXT, "expected_variant": TEXT}
CONFIG = {"application": TEXT, "version": TEXT, "branch": TEXT, "expected_format": TEXT, "expected_variant": TEXT}
SNAPSHOT = {"profiles": Items({"description": TEXT, "configs": Items(CONFIG)})}
RECORD = {
    "summary": {"profiles": Items({"description": TEXT})},
    "to_dict": SNAPSHOT,
    "round_trip": SNAPSHOT,
    "matching_configs": Items(APPLIED),
    "find_config": Items({"matches": Items(APPLIED)}),
}


def _replace(python: object, port: object, order: object, schema: object = RECORD) -> tuple[object, object, bool]:
    """The Python value with each replaceable text field ``schema`` names taken from the port, its key order (``py_dump._key_order``'s
    shape, where a replaced field's child becomes None, the order of the str the port holds) and whether anything was replaced."""

    if isinstance(schema, dict) and isinstance(python, dict) and isinstance(port, dict):
        changed = False
        result = {}
        children = {entry[0]: index for index, entry in enumerate(order)} if isinstance(order, list) else {}
        new_order = [list(entry) for entry in order] if isinstance(order, list) else order
        for key, value in python.items():
            index = children.get(key)
            child = new_order[index][1] if index is not None else None
            field = schema.get(key)
            if field == TEXT and value is not None and not isinstance(value, str) and port.get(key) == str(_in_order(value, child)):
                result[key] = port[key]
                child, inner = None, True
            elif field is not None and field != TEXT and key in port:
                result[key], child, inner = _replace(value, port[key], child, field)
            else:
                result[key], inner = value, False
            if inner and index is not None:
                new_order[index][1] = child
            changed |= inner
        return result, new_order, changed
    if isinstance(schema, Items) and isinstance(python, list) and isinstance(port, list) and len(python) == len(port):
        orders = order if isinstance(order, list) and len(order) == len(python) else [None] * len(python)
        items = [_replace(value, other, child, schema.item) for value, other, child in zip(python, port, orders, strict=True)]
        changed = any(inner for _, _, inner in items)
        new_order = [child for _, child, _ in items] if changed and isinstance(order, list) else order
        return [item for item, _, _ in items], new_order, changed
    return python, order, False


def _in_order(value: object, order: object) -> object:
    # The dump is written with sort_keys, so a mapping's insertion order (which str() shows) is rebuilt from its key order.
    if isinstance(value, dict) and isinstance(order, list):
        children = {entry[0]: entry[1] for entry in order}
        return {key: _in_order(value[key], children[key]) for key in children if key in value}
    if isinstance(value, list) and isinstance(order, list) and len(order) == len(value):
        return [_in_order(item, child) for item, child in zip(value, order, strict=True)]
    return value


def _identifier_collision(python: object, port: object) -> bool:
    """Recorded divergence ("Detection profiles", text identifiers): the port raises the store's duplicate error where Python registered
    two identifiers, or two profile names, that are distinct values with one ``str()`` text (``1`` and ``"1"``). True only when the port
    record is exactly that ``ValueError`` and Python's ``to_dict`` holds such a pair: two configs of the named profile, a config of the
    named profile and one of another profile, or two profiles."""

    if not isinstance(python, dict) or not isinstance(port, dict) or "error" in python or set(port) != {"error"}:
        return False
    error = port["error"]
    snapshot = python.get("to_dict")
    if not isinstance(error, dict) or error.get("type") != "ValueError" or not isinstance(snapshot, dict):
        return False
    message = error.get("message")
    profiles = [entry for entry in snapshot.get("profiles", []) if isinstance(entry, dict)]

    def distinct_pairs(values: list[object]) -> list[str]:
        texts = []
        for index, first in enumerate(values):
            for second in values[index + 1:]:
                same_value = type(first) is type(second) and first == second
                if str(first) == str(second) and not same_value and (isinstance(first, str) or isinstance(second, str)):
                    texts.append(str(first))
        return texts

    for profile in profiles:
        ids = [config.get("id") for config in profile.get("configs", []) if isinstance(config, dict)]
        name = profile.get("name")
        if any(message == f"Duplicate config identifier {text!r} within profile {str(name)!r}" for text in distinct_pairs(ids)):
            return True
        for other in profiles:
            if other is profile:
                continue
            other_ids = [config.get("id") for config in other.get("configs", []) if isinstance(config, dict)]
            for first in other_ids:
                for second in ids:
                    if distinct_pairs([first, second]) and message == (
                        f"Config identifier {str(second)!r} already registered under profile {str(other.get('name'))!r}"
                    ):
                        return True
    names = [profile.get("name") for profile in profiles]
    return any(message == f"Profile {text!r} is already registered" for text in distinct_pairs(names))


def _lines(path: str) -> list[str]:
    # One record per "\n": a record may hold a raw U+0085, U+2028 or U+2029, which str.splitlines() would also split on.
    with open(path, encoding="utf-8", newline="") as handle:
        text = handle.read()
    lines = text.split("\n")
    return lines[:-1] if lines and lines[-1] == "" else lines


def _normalise(python_lines: list[str], port_lines: list[str], out: list[str]) -> int:
    """Appends each normalised Python record line to ``out``; returns the number of records changed."""

    count = 0
    for index, line in enumerate(python_lines):
        if index < len(port_lines) and line != port_lines[index]:
            python_record, port_record = json.loads(line), json.loads(port_lines[index])
            port_fields = port_record
            if isinstance(port_record, dict):
                port_fields = {key: value for key, value in port_record.items() if key != "key_order"}
            if _identifier_collision(python_record, port_fields):
                count += 1
                out.append(port_lines[index])
                continue
            order = python_record.pop("key_order", None) if isinstance(python_record, dict) else None
            if isinstance(port_record, dict):
                port_record.pop("key_order", None)
            record, order, changed = _replace(python_record, port_record, order)
            if changed:
                count += 1
                if order is not None:
                    record["key_order"] = order
                line = json.dumps(record, sort_keys=True, ensure_ascii=False)
        out.append(line)
    return count


def _self_test() -> int:
    """A text field at its position is replaced; the same stringified value under a text field's name inside a profile's or a config's
    metadata, or under a key the schema does not name, still differs after normalisation."""

    def profile(description: object, version: object, metadata: object) -> dict[str, object]:
        config = {"id": "c", "version": version, "metadata": metadata}
        return {"name": "p", "description": description, "metadata": metadata, "configs": [config]}

    def record(description: object, version: object, metadata: object, extra: object) -> str:
        snapshot = {"profiles": [profile(description, version, metadata)]}
        return json.dumps({"to_dict": snapshot, "round_trip": snapshot, "extra": {"version": extra}}, sort_keys=True)

    checks = [
        ("text fields at their positions are replaced", record(2, 1.5, {}, "x"), record("2", "1.5", {}, "x"), True),
        ("a metadata value under a text field's name is compared", record("d", "v", {"version": 1}, "x"), record("d", "v", {"version": "1"}, "x"), False),
        ("a metadata description is compared", record("d", "v", {"description": [1]}, "x"), record("d", "v", {"description": "[1]"}, "x"), False),
        ("a text field's name outside the schema is compared", record("d", "v", {}, 1), record("d", "v", {}, "1"), False),
    ]
    failures = 0
    for label, python_line, port_line, should_match in checks:
        out: list[str] = []
        _normalise([python_line], [port_line], out)
        matched = json.loads(out[0]) == json.loads(port_line)
        if matched != should_match:
            failures += 1
            print(f"self-test failed: {label}")
        else:
            print(f"self-test ok: {label}")
    return 1 if failures else 0


def main() -> int:
    if sys.argv[1:] == ["--self-test"]:
        return _self_test()
    out: list[str] = []
    count = _normalise(_lines(sys.argv[1]), _lines(sys.argv[2]), out)
    for line in out:
        sys.stdout.write(line + "\n")
    sys.stderr.write(f"{count}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
