"""Generate the CPython oracle data the C# Python-regex, hunt and secret-scanning tests compare against.

Temporary; deleted together with the Python package. Re-run after editing a case table:
    python tools/parity/gen_regex_cases.py

Writes gui/DriftBuster.Backend.Tests/Infrastructure/Data/python_regex_cases.json. Every string is written with
``ensure_ascii=True`` so unpaired surrogates survive as ``\\uXXXX`` escapes (the C# tests read the file with
``PythonJson``). Spans are code-point offsets, as Python reports them.

Sections:
- ``casing``: every code point whose ``_sre.unicode_tolower`` differs from itself, and the ranges ``_sre.unicode_iscased``
  accepts.
- ``nonprintable``: code point ranges ``str.isprintable()`` rejects.
- ``categories``: every code point's general category as runs ``[first, last, category]`` covering 0..0x10FFFF; the C# oracle test
  derives the port's Unicode 15.1 delta table from these runs and the runtime's categories, and fails with the table to paste.
- ``decimal``: runs ``[first, last, value of first]`` of code points with a ``unicodedata.decimal`` value (``int()``, ``float()``
  and ``str.format`` digits), consecutive values within a run.
- ``digit``: the same runs for ``unicodedata.digit`` (``str.isdigit()``).
- ``alpha`` / ``alnum``: code point ranges ``str.isalpha()`` / ``str.isalnum()`` accept.
- ``atoms``: for single-character patterns, the ranges of code points ``fullmatch`` accepts.
- ``patterns``: ``finditer`` and ``search`` results (span, groups, lastindex) or the compile error, per pattern and text.
- ``pure_path``: ``PurePosixPath`` ``match`` results (or the error), and ``parts`` / ``str`` / ``parent`` / ``relative_to``.
"""

from __future__ import annotations

import _sre
import json
import random
import re
import unicodedata
from pathlib import Path, PurePosixPath

HERE = Path(__file__).resolve().parent
OUT = HERE.parents[1] / "gui" / "DriftBuster.Backend.Tests" / "Infrastructure" / "Data"

MAX_CP = 0x110000


def _ranges(codes: list[int]) -> list[list[int]]:
    out: list[list[int]] = []
    for code in codes:
        if out and out[-1][1] + 1 == code:
            out[-1][1] = code
        else:
            out.append([code, code])
    return out


def casing() -> dict[str, object]:
    lower = [[cp, _sre.unicode_tolower(cp)] for cp in range(MAX_CP) if _sre.unicode_tolower(cp) != cp]
    cased = _ranges([cp for cp in range(MAX_CP) if _sre.unicode_iscased(cp)])
    return {"lower": lower, "cased": cased}


def nonprintable() -> list[list[int]]:
    return _ranges([cp for cp in range(MAX_CP) if not chr(cp).isprintable()])


def categories() -> list[list[object]]:
    runs: list[list[object]] = []
    for cp in range(MAX_CP):
        category = unicodedata.category(chr(cp))
        if runs and runs[-1][2] == category:
            runs[-1][1] = cp
        else:
            runs.append([cp, cp, category])
    return runs


def _value_runs(lookup) -> list[list[int]]:
    runs: list[list[int]] = []
    for cp in range(MAX_CP):
        value = lookup(chr(cp), -1)
        if value < 0:
            continue
        if runs and runs[-1][1] + 1 == cp and runs[-1][2] + (cp - runs[-1][0]) == value:
            runs[-1][1] = cp
        else:
            runs.append([cp, cp, value])
    return runs


def decimal() -> list[list[int]]:
    return _value_runs(unicodedata.decimal)


def digit() -> list[list[int]]:
    return _value_runs(unicodedata.digit)


def alpha() -> list[list[int]]:
    return _ranges([cp for cp in range(MAX_CP) if chr(cp).isalpha()])


def alnum() -> list[list[int]]:
    return _ranges([cp for cp in range(MAX_CP) if chr(cp).isalnum()])


ATOMS: list[str] = [
    r"\w", r"\W", r"\d", r"\D", r"\s", r"\S", r".", r"(?s).",
    r"(?a)\w", r"(?a)\W", r"(?a)\d", r"(?a)\s", r"(?a)\S",
    r"[^a]", r"[\w]", r"[^\W\d]", r"[\s,;]", r"[^'\"]", r"[^'\"\n]", r"[^>]", r"[^;]",
    r"(?i)k", r"(?i)K", r"(?i)\u212a", r"(?i)s", r"(?i)i", r"(?i)\u0130", r"(?i)\u0131", r"(?i)\u00df",
    r"(?i)\u03c3", r"(?i)\u0345", r"(?i)\u00b5", r"(?i)\U00010400", r"(?i)\u01c5", r"(?i)1", r"(?i)[^k]",
    r"(?i)[a-z]", r"(?i)[A-Z]", r"(?i)[0-9a-f]", r"(?i)[k]", r"(?i)[\u0131]", r"(?i)[\u0100-\u017f]",
    r"(?i)[\U00010400-\U0001044f]", r"(?i)[\u00c0-\U0001044f]", r"(?i)[^a-z]", r"(?i)[a-z\d]", r"(?i)[^\s]",
    r"(?i)[\w\-\.]", r"(?i)[a-z0-9_-]", r"(?i)[A-Za-z0-9\-_/+=]", r"(?i)[\u212a]", r"(?i)[\W]", r"(?i)[a\W]",
    r"(?i)[^a\W]", r"(?i)\w", r"(?i)[\U0001e900-\U0001e943]", r"(?i)[\U0001e922]", r"(?i)\U0001e922",
    r"(?i)[\u24b6-\u24e9]", r"(?i)[\u2160-\u217f\d]",
    r"(?ia)[a-z]", r"(?ia)k", r"(?ia)[^k]", r"(?ia)\w", r"(?ia)[\u0100-\u017f]", r"(?ia)[a-z\U00010400]",
    r"[\ud800-\udbff]", r"[^\ud800]", r"\ud83d", r"\udc00", r"(?i)[\ud800-\udfff]",
]


def atoms() -> list[dict[str, object]]:
    chars = [chr(cp) for cp in range(MAX_CP)]
    out: list[dict[str, object]] = []
    for pattern in ATOMS:
        compiled = re.compile(pattern)
        fullmatch = compiled.fullmatch
        codes = [cp for cp, ch in enumerate(chars) if fullmatch(ch)]
        out.append({"pattern": pattern, "ranges": _ranges(codes)})
    return out


HUNT_PATTERNS: list[tuple[str, int]] = [
    (r"\b[a-z0-9_-]+\.(local|lan|corp|com|net|internal)\b", re.I | re.M),
    (r"\b[0-9a-f]{40}\b", re.I | re.M),
    (r"\b[0-9a-f]{64}\b", re.I | re.M),
    (r"\b\d+\.\d+\.\d+(?:\.\d+)?\b", re.I | re.M),
    (r"[A-Za-z]:\\[\w\-\.\s]+", re.I | re.M),
    (r"[A-Za-z]:\\\\[\\w\\-\\.\\s]+", re.I | re.M),
    (r"/opt/[\w\-\.]+", re.I | re.M),
    (r"""connectionstring\s*=\s*['"][^'"\n]+['"]""", re.I | re.M),
    (r"""<endpoint\b[^>]*\baddress\s*=\s*['"][^'\"]+['"]""", re.I | re.M),
    (r"""(endpoint|serviceurl|baseaddress)\s*=\s*['"][^'\"]+['"]""", re.I | re.M),
    (
        r"""key\s*=\s*['"][^'\"]*(endpoint|serviceurl|baseaddress|address)[^'\"]*['"][^\n]*value\s*=\s*['"][^'\"]+['"]""",
        re.I | re.M,
    ),
    (r"""key\s*=\s*['"][^'\"]*(feature|flag|toggle)[^'\"]*['"][^\n]*value\s*=\s*['"][^'\"]+['"]""", re.I | re.M),
    (r"""<feature\b[^>]*\b(enabled|value)\s*=\s*['"][^'\"]+['"]""", re.I | re.M),
    (r"Server=([^;]+)", re.I | re.M),
]

SECRET_PATTERNS: list[tuple[str, int]] = [
    (r"(?i)password\s*[:=]\s*['\"]?[A-Za-z0-9\-_/+=]{8,}", 0),
    (r"(?i)(api|auth|token)[-_ ]?(key|token)\s*[:=]\s*['\"]?[A-Za-z0-9]{16,}", 0),
    (r"AKIA[0-9A-Z]{16}", 0),
    (r"[\s,;]+", 0),
]

HUNT_TEXTS: list[str] = [
    "Server host: api.corp.local",
    "Thumbprint: 0123456789abcdef0123456789abcdef01234567",
    "certificate thumbprint=0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
    "version: 1.2.3 and 10.20.30.40 and 1.2.3.4.5",
    r"install path C:\Program Files\Vendor",
    r"install_path=D:\\Apps\\Tool-1.0\\bin;",
    "path /opt/vendor-app.v2/bin",
    '<add name="Primary" connectionString="Server=sql.example.local;Database=App;" />',
    '<add key="ServiceEndpoint" value="https://api.example.com/v1/" />',
    '<add key="FeatureFlag:NewDashboard" value="true" />',
    '<endpoint address="net.tcp://svc.example.local:9000/Feed" />',
    "baseAddress = 'http://x.corp'",
    '<feature name="x" enabled="true"/>',
    "SERVER=\u212aube.LOCAL host",
    "host \u00e9t\u00e9.corp h\u00f4te.com _x.net",
    "host a\u0301.lan \U0001d400.internal",
    "host \U0001f600.com x.com\U0001f600",
    "host \u0130stanbul.corp paſſ.net",
    "server a.local\nb.local\r\nc.local\n",
    "0123456789abcdef0123456789abcdef01234567\u0663",
    "version \u0661.\u0662.\u0663 \U0001d7ce.1.2",
    "connectionString = 'a\"b'",
    "connectionString='x\ny'",
    "Server=db.internal.local;Database=Main;",
    "",
    "\ud83d host.local \udc00",
]

SECRET_TEXTS: list[str] = [
    "password = Hunter12345\n",
    "PASSWORD: 'abcdefgh'",
    "pa\u017f\u017fword=abcdefghij",
    "pass\u212aword=abcdefghij",
    "api_key: ABCDEFGHIJKLMNOP1234",
    "Auth Token = abcdefghijklmnop",
    "tokentoken=abcdefghijklmnopq",
    "AKIAABCDEFGHIJKLMNOP",
    "akiaABCDEFGHIJKLMNOP",
    "a, b ; c",
    " \x1c x,\u2028;y ",
    "",
]

ADVERSARIAL: list[tuple[str, int, list[str]]] = [
    (r"a??", 0, ["a", "aa", ""]),
    (r"x*", 0, ["axb", "", "xx"]),
    (r"(a)|(b)", 0, ["ab", "ba"]),
    (r"((a)b)", 0, ["ab"]),
    (r"(?:(a)|b)+", 0, ["ab", "ba", "bab"]),
    (r"(a|)+", 0, ["aa", "b"]),
    (r"(a*)*", 0, ["aab", "b"]),
    (r"(a*)+", 0, ["aab", "b"]),
    (r"(?:(a)(b)?)+", 0, ["aba", "abab"]),
    (r"(?=(ab))(a)", 0, ["ab"]),
    (r"(ab)(?<=(a)b)", 0, ["ab"]),
    (r"(a)(?=(b))", 0, ["ab"]),
    (r"\bfoo\b", 0, ["foo", "a foo.", "\u00e9foo", "foo\U0001d400", "_foo", "\u0301foo"]),
    (r"\Bo\B", 0, ["foo", "o"]),
    (r"\B", 0, ["", "ab", " "]),
    (r"\b", 0, ["", "ab", " "]),
    (r".{2}", 0, ["\U0001f600\U0001f601x", "a\ud83d\U0001f600"]),
    (r"(?s).{2}", 0, ["a\nb"]),
    (r"^\w+$", re.M, ["a\nbb\r\nccc\n", "x\n"]),
    (r"^", re.M, ["a\n", "\n\n"]),
    (r"$", 0, ["a\n", "a\n\n", "a"]),
    (r"$", re.M, ["a\nb\n"]),
    (r"a\Z", 0, ["a\n", "a"]),
    (r"\Aa", re.M, ["a\na"]),
    (r"(?x) a b # comment\n c", 0, ["abc", "a b c"]),
    (r"a++a", 0, ["aaa"]),
    (r"(?>a|ab)c", 0, ["abc", "ac"]),
    (r"(?P<n>a)(?P=n)", 0, ["aa", "aA"]),
    (r"(?i)(?P<n>a)(?P=n)", 0, ["aA", "kK"]),
    (r"(a)?(?(1)b|c)", 0, ["ab", "c", "ac"]),
    (r"(?<=\$)\d+", 0, ["$100 $x $2"]),
    (r"(?<!\w)\d+", 0, ["a1 2 _3"]),
    (r"[^\n]+", 0, ["a\nb\r\nc"]),
    (r"(?i)stra\u00dfe", 0, ["STRASSE", "stra\u1e9ee", "STRA\u00dfE"]),
    (r"(?i)\u03c3+", 0, ["\u03a3\u03c2\u03c3"]),
    (r"(?i:a)b", 0, ["Ab", "AB"]),
    (r"(?i)a(?-i:b)", 0, ["Ab", "AB"]),
    (r"(?a)\w+", 0, ["\u00e9t\u00e9 ok"]),
    (r"(?a:\w)\w", 0, ["\u00e9\u00e9 a\u00e9"]),
    (r"\x41\u0042\U00000043\101\0", 0, ["ABCA\x00"]),
    (r"[\x41-\x43]{2}", 0, ["ABCD"]),
    (r"a{,2}", 0, ["aaa"]),
    (r"a{2}", 0, ["aaa"]),
    (r"a{}", 0, ["a{}"]),
    (r"a{1,x}", 0, ["a{1,x}"]),
    (r"a{2,3}?", 0, ["aaaa"]),
    (r"(|a)", 0, ["a"]),
    (r"", 0, ["ab", ""]),
    (r"[]a]", 0, ["]a"]),
    (r"[a-]", 0, ["-a"]),
    (r"[\]]", 0, ["]"]),
    (r"\.", 0, ["a.b"]),
    (r"(?#comment)x", 0, ["x"]),
    (r"(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)(k)\11", 0, ["abcdefghijkk"]),
    (r"(?:ab|ac)+", 0, ["abacab"]),
    (r"(?i)[\u0130]", 0, ["iI\u0131\u0130"]),
    (r"(?i)x\u0130", 0, ["XI", "xi\u0307"]),
    (r"\d+", 0, ["\u0661\u0662 \U0001d7ce 12 \u00b2"]),
    (r"\s+", 0, [" \x1c\x1d\x1e\x1f\x85\u2028\u200b"]),
    (r"(?u)\w", 0, ["a"]),
    (r"[", 0, [""]),
    (r"(", 0, [""]),
    (r")", 0, [""]),
    (r"a**", 0, [""]),
    (r"*", 0, [""]),
    (r"\q", 0, [""]),
    (r"(?<=a*)b", 0, [""]),
    (r"(?<=a|bc)b", 0, [""]),
    (r"(?P<1>x)", 0, [""]),
    (r"\8", 0, [""]),
    (r"(?L)x", 0, [""]),
    (r"x{2,1}", 0, [""]),
    (r"(?i", 0, [""]),
    (r"a(?i)b", 0, [""]),
    (r"(a)\2", 0, [""]),
    (r"(a\1)", 0, [""]),
    (r"[b-a]", 0, [""]),
    (r"[\d-z]", 0, [""]),
    (r"\x4", 0, [""]),
    (r"\U00110000", 0, [""]),
    (r"(?P=missing)", 0, [""]),
    (r"(?(2)a|b)", 0, [""]),
    (r"(?(1)a|b|c)", 0, [""]),
    (r"x{4294967295}", 0, [""]),
    (r"(?<=(a))b", 0, ["ab"]),
    (r"\N{EM DASH}", 0, ["\u2014"]),
    (r"[\N{EM DASH}]", 0, ["\u2014"]),
    (r"(?i-s:a.)b", re.S, ["A\nb", "a\nB"]),
    (r"(?-i:a)b", re.I, ["aB", "AB"]),
    (r"(?au)x", 0, [""]),
    (r"(?i-a:x)", 0, [""]),
    (r"(?i-i:x)", 0, [""]),
    (r"(?-)", 0, [""]),
    (r"(?-x", 0, [""]),
    (r"(?-", 0, [""]),
    (r"(?i-q:x)", 0, [""]),
    (r"(?i-:x)", 0, [""]),
    (r"(?i-s$", 0, [""]),
    (r"(?i$", 0, [""]),
    (r"(?iq)", 0, [""]),
    (r"(?i", 0, [""]),
    (r"(?x)a b\n# c\nd", 0, ["abd", "ab d"]),
    (r"(?x: a b )c", 0, ["abc"]),
    (r"(?x)(?-x:a b)c", 0, ["a bc"]),
    (r"(?x)[ ]a", 0, [" a"]),
    (r"(?P<a>x)(?P<a>y)", 0, [""]),
    (r"(?Px)", 0, [""]),
    (r"(?P", 0, [""]),
    (r"(?", 0, [""]),
    (r"(?<x)", 0, [""]),
    (r"(?<", 0, [""]),
    (r"(?#abc", 0, [""]),
    (r"(?<!ab)c", 0, ["abc xbc c"]),
    (r"x(?!)", 0, ["x"]),
    (r"x(?=)", 0, ["x"]),
    (r"(?<=(?P<g>a))(?P=g)", 0, ["aa"]),
    (r"(?<=(a)\1)b", 0, [""]),
    (r"(?<=(?P<n>a)(?P=n))b", 0, [""]),
    (r"(a)(?<=\1)b", 0, ["ab"]),
    (r"(?(a)b)", 0, [""]),
    (r"(?P<a>x)?(?(a)y|z)", 0, ["xy", "z", "xz"]),
    (r"(?(0)a)", 0, [""]),
    (r"(?(1a)b)", 0, [""]),
    (r"(a)(?(1)b", 0, [""]),
    (r"(?(00000000001)a|b)(x)", 0, ["bx"]),
    (r"(?q)", 0, [""]),
    (r"(a", 0, [""]),
    (r"(?:a", 0, [""]),
    (r"(?P<>x)", 0, [""]),
    (r"(?P<a", 0, [""]),
    (r"(?P=)", 0, [""]),
    (r"(?P=a", 0, [""]),
    (r"(?P<a>b(?P=a))", 0, [""]),
    (r"\a\f\v\t\r\n\\", 0, ["\a\f\v\t\r\n\\"]),
    (r"[\a\b\f\v\t\r\n\\]+", 0, ["\a\b\f\v\t\r\n\\"]),
    (r"[\x41-C][\101][\-][\U0001F600]", 0, ["BA-\U0001f600"]),
    (r"[\400]", 0, [""]),
    (r"\400", 0, [""]),
    (r"[\8]", 0, [""]),
    (r"[\A]", 0, [""]),
    (r"[\q]", 0, [""]),
    (r"[\u12]", 0, [""]),
    (r"\u00e9\x", 0, [""]),
    (r"[\U00110000]", 0, [""]),
    (r"\07\08", 0, ["\x07\x008"]),
    (r"(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)(k)(l)\12", 0, ["abcdefghijkll"]),
    (r"\12", 0, [""]),
    (r"a{,}", 0, ["aaa"]),
    (r"x{2}?y", 0, ["xxy"]),
    (r"x*+x", 0, ["xxx"]),
    (r"x?+x", 0, ["x", "xx"]),
    (r"(?:)*a", 0, ["a"]),
    (r"^*", 0, [""]),
    (r"\b+", 0, [""]),
    (r"x{99999999999999999999}", 0, [""]),
    (r"(?<=a|b)c", 0, ["ac bc cc"]),
    (r"(?<=(?:ab){2})c", 0, ["ababc abc"]),
    (r"(?<=(a)(?(1)b|c))d", 0, ["abd"]),
    (r"(?<=(?>ab))c", 0, ["abc"]),
    (r"(?<=a(?(1)b))c", 0, [""]),
    (r"a\\", 0, ["a\\"]),
    ("a\\", 0, [""]),
    (r"[a", 0, [""]),
    (r"[a-", 0, [""]),
    (r"[^]a]+", 0, ["b]a c"]),
    (r"[\w-]+", 0, ["a-b c"]),
    (r"[a-\d]", 0, [""]),
    (r"(?a:[^\W\d])+", 0, ["ab1\u00e9"]),
    (r"(?s:.)(?-s:.)", 0, ["\n\n", "\nx"]),
    (r"a|b|", 0, ["c"]),
    (r"(?:a|)(?:b|)", 0, ["b"]),
    (r"(?i)(\u212a)\1", 0, ["kK"]),
    # Lazy and greedy repeats over bodies that can match empty (last_ptr zero-width protection).
    (r"b(?:x?)+?-", 0, ["b-", "bx-", "bxx-x-"]),
    (r"b(?:(?:.?)+?-)", 0, ["b-", "b--"]),
    (r"[^0-9a-z.](?= *(?-i:.?)+?[\W]-*)", re.I, ["Ka_'", "K-a"]),
    (r"b(?=(?:.?)+?-)", 0, ["b-", "bb-"]),
    (r"(?:(a?)+?)*", 0, ["", "a", "aa", "ba"]),
    (r"((a?)+?){,2}", 0, ["", "a", "aaa"]),
    (r"(?-i:((\u00df{,2}|:??)+?k*|\u00e9{2})\w){,2}", re.I | re.M, ["\u00df\u00dfk\u00e9\u00e9:kx\u00df\u212a:\u00df\u00df\u00dfk", "\u00dfk:k"]),
    (r"(|a)*", 0, ["a", "aa"]),
    (r"(?:|a)*", 0, ["a"]),
    (r"(?:a|)*?b", 0, ["aab", "b"]),
    # Possessive counted repeats: each iteration is atomic, earlier iterations are never re-entered.
    (r"(?:x{1,3}){2}+y", 0, ["xxy", "xxxxy", "xxxxxxy"]),
    (r"(?:xx|x){2}+y", 0, ["xxy", "xxxy"]),
    (r"(?:x{1,3})++y", 0, ["xxy"]),
    (r"(?:x|xx){2}+y", 0, ["xxy", "xxxy"]),
    (r"(?:\d{1,2}){2}+", 0, ["1111\t12\r\n34", "12", "123"]),
    # Case-insensitive back-references compare sre_lower_unicode of each code point.
    (r"(?i)(\u0131)\1", 0, ["\u0131I", "\u0131i"]),
    (r"(?i)(s)\1", 0, ["s\u017f", "sS"]),
    (r"(?i)(k)\1", 0, ["k\u212a"]),
    (r"(?i)(\u03c3)\1", 0, ["\u03c3\u03c2", "\u03c3\u03a3"]),
    (r"(?ia)(k)\1", 0, ["kK", "k\u212a"]),
    # lastindex with groups inside repeated look-arounds and alternations.
    (r"((?=(a)))*a", 0, ["a"]),
    (r"(?=(a))a|(b)", 0, ["a", "b"]),
    (r"(?:(a)|(b))+", 0, ["ab", "ba"]),
    # Named Unicode escapes.
    (r"\N{em dash}", 0, ["\u2014"]),
    (r"\N{Latin Small Letter A}+", 0, ["aAa"]),
    (r"\N{LINE FEED}", 0, ["\n"]),
    (r"\N{BYTE ORDER MARK}", 0, ["\ufeff"]),
    (r"[\N{DIGIT ZERO}-\N{DIGIT NINE}]{4}", 0, ["pin 0000 here"]),
    (r"\N{DIGIT ZERO}{4}", 0, ["pin 0000 here"]),
    (r"\N{HANGUL SYLLABLE GAG}", 0, ["\uac01"]),
    (r"\N{hangul syllable ga}", 0, [""]),
    (r"\N{HANGUL SYLLABLE GAX}", 0, [""]),
    (r"\N{CJK UNIFIED IDEOGRAPH-4E00}", 0, ["\u4e00"]),
    (r"\N{CJK UNIFIED IDEOGRAPH-4e00}", 0, [""]),
    (r"\N{CJK UNIFIED IDEOGRAPH-2A6E0}", 0, [""]),
    (r"\N{KEYCAP NUMBER SIGN}", 0, [""]),
    (r"\N{ EM DASH}", 0, [""]),
    (r"\N{}", 0, [""]),
    (r"\N{EM DASH", 0, [""]),
    (r"\N", 0, [""]),
    ("\\N{EM DASH\ud800}", 0, [""]),
    ("[\\N{\udc00}]", 0, [""]),
    ("\\N{" + "A" * 257 + "}", 0, [""]),
]


# Top-level greedy single-character repeats with position-only tails, where the matcher reuses tail failures across start
# positions and finditer steps: fixed cases for anchors, empty matches, must-advance steps, groups before the repeat, bounded
# and two-repeat tails, plus a seeded grid of pattern shapes over random texts.
POSITIONAL_TAILS: list[tuple[str, int, list[str]]] = [
    (r"k[^\n]*v", re.I | re.M, ["key v", "kkvv", "k\nv", "kv kv k", "", "vk", "kKK\u212avV"]),
    (r"a.*b", 0, ["aabb ab", "a\nb", "ab" * 5]),
    (r".*x", 0, ["xx", "", "\nx", "abx\ncx"]),
    (r"[^\n]*", 0, ["ab\ncd", "", "\n\n"]),
    (r"x*", 0, ["axxb", ""]),
    (r"a*$", re.M, ["baa\naa", ""]),
    (r"\w*\b", 0, ["ab cd", ""]),
    (r"(a)[^\n]*b", 0, ["a1b a2b", "aab"]),
    (r"(?:ab)?[^\n]*c", 0, ["abxc", "c"]),
    (r"[ab]{2,5}c", 0, ["ababababc", "aaaaaac", "abc"]),
    (r"x[^\n]*\bend\b", re.I, ["x the end, x ends, x END", "xend"]),
    (r"q[^\n]*z[^\n]*z", 0, ["qzzz", "qz qz", "qaz"]),
    (r"[^\n]*?v", 0, ["avbv"]),
    (r".*(?=x)", 0, ["axbx"]),
    (r"k[^\n]{0,3}v", 0, ["k12v k1234v kv"]),
    (r"[^\n]*\d+$", re.M, ["a1 b22\nc333"]),
    (r"key\s*=\s*['\"][^'\"]*(feature|flag|toggle)[^'\"]*['\"][^\n]*value\s*=\s*['\"][^'\"]+['\"]", re.I | re.M,
     ['key="flag" v ' * 40, 'key="flag" value="1" key="toggle" value=\'x\' ' * 3, 'key="flag" v key="flag" value="z"']),
    # Tails holding groups and alternations (the feature-flag element pattern among them).
    (r"<feature\b[^>]*\b(enabled|value)\s*=\s*['\"][^'\"]+['\"]", re.I | re.M,
     ["<feature enabled=x " * 40, "<feature value " * 30, '<feature a="1" enabled="true"/> <feature value=\'2\'>', "<featurex value='1'"]),
    (r"a[^\n]*(b|c)(d)", 0, ["abd acd a", "a" * 20 + "cd", "abcbd", "ab\ncd"]),
    (r"a.*(x(y)|z)w", 0, ["axyw azw axw", "azzw axyzw", "a" + "xy" * 10]),
    (r"(k)[^\n]*(?:v|w)$", re.M, ["kv\nkw\nkx", "kvvw kwx"]),
    (r"[^\n]*(?i:q)(r|)s", 0, ["QsQrs qs", "rs"]),
]


def _positional_grid() -> list[tuple[str, int, list[str]]]:
    rng = random.Random(20260913)
    out = []
    for lead in ("", "k", "(k)"):
        for repeat in (r"[^\n]*", ".*", "[a-c]*", r"[^\n]+", r"\w{1,4}", "[ab]*"):
            for tail in ("v", "va", "v$", r"\bv", "v+", "(?:v)", "", "a[bc]*d"):
                for flags in (0, re.I | re.M):
                    texts = ["".join(rng.choice("kvabcd \nV") for _ in range(rng.randint(0, 40))) for _ in range(4)]
                    out.append((lead + repeat + tail, flags, texts))
    return out


def _match_record(match: re.Match[str]) -> dict[str, object]:
    return {
        "span": list(match.span()),
        "groups": [match.group(0), *match.groups()],
        "lastindex": match.lastindex,
    }


def _pattern_case(pattern: str, flags: int, texts: list[str]) -> dict[str, object]:
    record: dict[str, object] = {"pattern": pattern, "flags": flags}
    try:
        compiled = re.compile(pattern, flags)
    except re.error as exc:
        record["error"] = {"type": "error", "message": exc.msg, "pos": exc.pos}
        return record
    except (OverflowError, ValueError) as exc:
        record["error"] = {"type": type(exc).__name__, "message": str(exc), "pos": None}
        return record
    record["groups"] = compiled.groups
    record["groupindex"] = dict(compiled.groupindex)
    runs = []
    for text in texts:
        search = compiled.search(text)
        runs.append(
            {
                "text": text,
                "finditer": [_match_record(m) for m in compiled.finditer(text)],
                "search": None if search is None else _match_record(search),
            }
        )
    record["runs"] = runs
    return record


def patterns() -> list[dict[str, object]]:
    out = [_pattern_case(pattern, flags, HUNT_TEXTS) for pattern, flags in HUNT_PATTERNS]
    out.extend(_pattern_case(pattern, flags, SECRET_TEXTS) for pattern, flags in SECRET_PATTERNS)
    out.extend(_pattern_case(pattern, flags, texts) for pattern, flags, texts in ADVERSARIAL)
    out.extend(_pattern_case(pattern, flags, texts) for pattern, flags, texts in (*POSITIONAL_TAILS, *_positional_grid()))
    return out


PURE_PATH_MATCH: list[tuple[str, str]] = [
    ("a/b/c.txt", "*.txt"), ("a/b/c.txt", "b/*.txt"), ("a/b/c.txt", "a/*.txt"), ("/a/b/c.txt", "/a/*/*.txt"),
    ("/a/b/c.txt", "/*/*.txt"), ("a/.hidden", "*"), ("a/b.TXT", "*.txt"), ("a/b-c", "b[-]c"), ("a/bxc", "b[!a-c]c"),
    ("a/bbc", "b[!a-c]c"), ("a/b]c", "b[]]c"), ("a/b\\c", "b[\\]c"), ("a/x.log", "**.log"), ("a/x.log", "**/x.log"),
    ("x", "[a-]"), ("-", "[a-]"), ("a/b", "a/b/"), ("a/b", "./b"), ("a//b", "a/b"), ("ab", "a?b"), ("a.b", "a?b"),
    ("a[b", "a[b"), ("abc", "a[c-a]c"), ("abc", "a[z-a-c]c"), ("a&c", "a[&~|]c"), ("x/\U0001f600.txt", "*.txt"),
    ("x/\U0001f600.txt", "?.txt"), ("a", ""), ("/", "/"), ("a/b", "*/*/*"), ("notes/entry.log", "notes/entry.log"),
    ("a/b.c.d", "*.d"), ("a/(b)+c", "(b)+c"), ("a/b", "[!]"), ("a/!", "[!]"), ("a/b", "[]"), ("a/x-y", "x[--z]y"),
]

PURE_PATHS: list[tuple[str, str]] = [
    ("a/b/c.txt", "a"), ("/a/b", "/a"), ("a", "."), ("./a/./b/", "a"), ("//x/y", "//x"), ("///x", "/"), ("", "."), ("/", "/"),
    ("a/b", "c"), ("a/b", "a/b"),
]


def pure_path() -> dict[str, object]:
    matches = []
    for path, pattern in PURE_PATH_MATCH:
        try:
            matches.append({"path": path, "pattern": pattern, "result": PurePosixPath(path).match(pattern)})
        except ValueError as exc:
            matches.append({"path": path, "pattern": pattern, "error": str(exc)})
    paths = []
    for path, other in PURE_PATHS:
        pure = PurePosixPath(path)
        try:
            relative = pure.relative_to(other).as_posix()
        except ValueError:
            relative = None
        paths.append(
            {"path": path, "other": other, "parts": list(pure.parts), "str": str(pure), "parent": str(pure.parent), "relative_to": relative}
        )
    return {"match": matches, "paths": paths}


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    data = {
        "casing": casing(),
        "nonprintable": nonprintable(),
        "categories": categories(),
        "decimal": decimal(),
        "digit": digit(),
        "alpha": alpha(),
        "alnum": alnum(),
        "atoms": atoms(),
        "patterns": patterns(),
        "pure_path": pure_path(),
    }
    path = OUT / "python_regex_cases.json"
    path.write_text(json.dumps(data, ensure_ascii=True, separators=(",", ":")) + "\n", encoding="utf-8")
    print(f"wrote {path}")


if __name__ == "__main__":
    main()
