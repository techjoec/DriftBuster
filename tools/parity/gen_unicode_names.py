"""Generate the Unicode character name table behind the port's ``\\N{name}`` regex escape.

Temporary; deleted together with the Python package. The table it writes stays: it is data, not code.

Usage:
    python tools/parity/gen_unicode_names.py <NameAliases.txt>

``NameAliases.txt`` must be the file of the Unicode version the interpreter carries (``unicodedata.unidata_version``,
15.1.0 for CPython 3.13), from https://www.unicode.org/Public/<version>/ucd/NameAliases.txt.

Writes gui/DriftBuster.Backend/Resources/unicode_names.txt.gz: gzip-compressed UTF-8 lines ``<hex code point>;<name>``
for every name ``unicodedata.lookup`` resolves through its name table, which is every ``unicodedata.name`` except the
algorithmic CJK unified ideograph and Hangul syllable names (computed by the port as ``_getcode`` computes them) plus
every name alias. Named sequences are left out: ``re`` refuses them (``ord`` of a multi-character string). Each entry is
checked against ``unicodedata.lookup`` before it is written.
"""

from __future__ import annotations

import gzip
import sys
import unicodedata
from pathlib import Path

OUT = Path(__file__).resolve().parents[2] / "gui" / "DriftBuster.Backend" / "Resources" / "unicode_names.txt.gz"

# unicodedata.c is_unified_ideograph, CPython 3.13.
CJK_RANGES = [
    (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0x20000, 0x2A6DF), (0x2A700, 0x2B739), (0x2B740, 0x2B81D),
    (0x2B820, 0x2CEA1), (0x2CEB0, 0x2EBE0), (0x2EBF0, 0x2EE5D), (0x30000, 0x3134A), (0x31350, 0x323AF),
]


def _algorithmic(code: int) -> bool:
    return 0xAC00 <= code <= 0xD7A3 or any(low <= code <= high for low, high in CJK_RANGES)


def main(aliases_path: str) -> int:
    names: dict[str, int] = {}
    for code in range(0x110000):
        name = unicodedata.name(chr(code), None)
        if name is None or _algorithmic(code):
            continue
        if unicodedata.lookup(name) != chr(code):
            raise SystemExit(f"lookup({name!r}) does not return U+{code:04X}")
        names[name] = code
    for line in Path(aliases_path).read_text(encoding="utf-8").splitlines():
        body = line.split("#", 1)[0].strip()
        if not body:
            continue
        code_text, name, _kind = body.split(";")
        code = int(code_text, 16)
        if unicodedata.lookup(name) != chr(code):
            raise SystemExit(f"alias {name!r} does not resolve to U+{code:04X}")
        names[name] = code
    lines = "".join(f"{code:X};{name}\n" for name, code in sorted(names.items(), key=lambda item: (item[1], item[0])))
    OUT.write_bytes(gzip.compress(lines.encode("utf-8"), compresslevel=9, mtime=0))
    print(f"wrote {OUT} (unicodedata {unicodedata.unidata_version})")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    raise SystemExit(main(sys.argv[1]))
