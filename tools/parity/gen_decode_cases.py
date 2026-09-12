"""Generate the byte-array corpus under tools/parity/cases/decode/ for decode parity.

Temporary; deleted together with the Python package. Re-run after editing the case table:
    python tools/parity/gen_decode_cases.py
"""

from __future__ import annotations

from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE / "cases" / "decode"

BOM8 = b"\xef\xbb\xbf"
BOM16LE = b"\xff\xfe"
BOM16BE = b"\xfe\xff"


def _nul_density(length: int, zeros: int) -> bytes:
    body = bytearray(b"x" * length)
    step = max(1, length // max(zeros, 1))
    placed = 0
    for index in range(0, length, step):
        if placed >= zeros:
            break
        body[index] = 0
        placed += 1
    return bytes(body)


CASES: dict[str, bytes] = {
    "empty.bin": b"",
    "single-space.bin": b" ",
    "whitespace-only.bin": b" \t\r\n \n\t",
    "ascii-plain.txt": b"Plain ASCII text\nwith two lines\n",
    "ascii-crlf.txt": b"line one\r\nline two\r\n",
    "ascii-form-feed.txt": b"page one\x0cpage two\x1dthree\x1etwo\x1cone",
    "utf8-bom-ascii.txt": BOM8 + b"payload",
    "utf8-bom-only.bin": BOM8,
    "utf8-bom-double.bin": BOM8 + BOM8 + b"twice",
    "utf8-bom-invalid-tail.bin": BOM8 + b"ok\xff\xfe",
    "utf8-multibyte.txt": "héllo wörld — ünïcödé ✓\n".encode(),
    "utf8-cjk.txt": "設定ファイル\n構成\n".encode(),
    "utf8-emoji.txt": "key 🔑 value\n".encode(),
    "utf8-leading-feff-no-bom.txt": "﻿﻿value".encode(),
    "utf8-nfc-nfd.txt": "café café\n".encode(),
    "utf8-truncated-sequence.bin": "héllo".encode()[:-1],
    "utf8-overlong.bin": b"ab\xc0\xafcd",
    "utf8-surrogate-encoded.bin": b"ab\xed\xa0\x80cd",
    "utf16le-bom.txt": BOM16LE + "utf-16 little\n".encode("utf-16-le"),
    "utf16le-no-bom.txt": "utf-16 little no bom\n".encode("utf-16-le"),
    "utf16be-bom.txt": BOM16BE + "utf-16 big\n".encode("utf-16-be"),
    "utf16be-no-bom.txt": "utf-16 big no bom\n".encode("utf-16-be"),
    "utf16le-bom-odd-length.bin": BOM16LE + b"\x00",
    "utf16le-bom-lone-surrogate.bin": BOM16LE + b"\x00\xd8a\x00",
    "utf16be-bom-lone-surrogate.bin": BOM16BE + b"\xd8\x00\x00a",
    "utf16le-no-bom-odd.bin": "abc".encode("utf-16-le") + b"\x00",
    "utf16le-non-ascii.txt": BOM16LE + "ünï ✓ 設定".encode("utf-16-le"),
    "utf16be-astral.txt": BOM16BE + "🔑 key".encode("utf-16-be"),
    "utf16le-short.bin": b"A\x00",
    "utf16le-two-chars.bin": b"A\x00B\x00",
    "utf16-mixed-lanes.bin": b"A\x00\x00B\x00C",
    "latin1-high-bytes.txt": "café ñandú".encode("latin-1"),
    "latin1-all-high.bin": bytes(range(0xA0, 0xFF)),
    "latin1-mostly-ascii.txt": b"mostly ascii " + bytes([0xE9]) * 2,
    "binary-full-range.bin": bytes(range(256)),
    "binary-random-like.bin": bytes((index * 73 + 11) % 256 for index in range(512)),
    "binary-control-heavy.bin": bytes(range(0, 32)) * 4,
    "ascii-89-percent.bin": b"x" * 89 + b"\xff" * 11,
    "ascii-90-percent.bin": b"x" * 90 + b"\xff" * 10,
    "ascii-91-percent.bin": b"x" * 91 + b"\xff" * 9,
    "nul-24-percent.bin": _nul_density(100, 24),
    "nul-25-percent.bin": _nul_density(100, 25),
    "nul-26-percent.bin": _nul_density(100, 26),
    "nul-50-percent-interleaved.bin": b"".join(b"x\x00" for _ in range(50)),
    "nul-odd-lane-59-percent.bin": b"".join((b"x\x00" if index % 5 else b"xy") for index in range(50)),
    "nul-odd-lane-60-percent.bin": b"".join((b"x\x00" if index % 5 != 4 or index % 10 == 9 else b"xy") for index in range(50)),
    "nul-even-lane-ascii.bin": b"".join(b"\x00x" for _ in range(40)),
    "nul-only.bin": b"\x00" * 64,
    "nul-then-ascii.bin": b"\x00" * 30 + b"x" * 70,
    "del-char.bin": b"abc\x7fdef",
    "tab-lf-cr-only.bin": b"\t\n\r" * 10,
    "c1-controls.bin": b"abc" + bytes(range(0x80, 0xA0)),
    "nel-separator.txt": "line oneline two".encode(),
    "u2028-separator.txt": "line one line two three".encode(),
    "utf8-bom-utf16-body.bin": BOM8 + "text".encode("utf-16-le"),
    "utf16le-bom-utf8-body.bin": BOM16LE + "text".encode(),
}


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    for name, payload in CASES.items():
        (OUT / name).write_bytes(payload)
    print(f"wrote {len(CASES)} cases to {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
