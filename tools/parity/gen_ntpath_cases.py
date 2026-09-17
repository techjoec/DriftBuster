"""Generate the CPython oracle data the C# ``PythonNtPath`` and Windows-flavour ``PythonPurePath`` tests compare against.

Temporary; deleted together with the Python package. Re-run after editing a case table:
    python tools/parity/gen_ntpath_cases.py

Writes gui/DriftBuster.Backend.Tests/Infrastructure/Data/python_ntpath_cases.json. ``ntpath`` and ``PureWindowsPath`` touch no
file system, so their results are the same on every host and the tests run on Linux too.

Sections:
- ``ntpath``: per path, ``splitroot``, ``splitdrive``, ``isabs``, ``normpath`` and ``split``.
- ``join``: ``ntpath.join(a, b)`` per pair.
- ``pure_windows``: per ``(path, other)``, ``PureWindowsPath`` ``parts`` / ``str`` / ``parent`` / ``anchor`` / ``is_absolute`` /
  ``relative_to(other).as_posix()`` (null where it raises ``ValueError``) and ``str(path / other)``.
- ``match``: ``PureWindowsPath(path).match(pattern)`` (or the error).
"""

from __future__ import annotations

import json
import ntpath
from pathlib import Path, PureWindowsPath

HERE = Path(__file__).resolve().parent
OUT = HERE.parents[1] / "gui" / "DriftBuster.Backend.Tests" / "Infrastructure" / "Data"

PATHS = [
    "",
    ".",
    "..",
    "./a",
    "a/./b/..",
    "a/b\\c",
    "a\\..\\..\\b",
    "a\\b\\",
    "a//b",
    "a:b",
    "C:",
    "C:\\",
    "C:/",
    "C:x",
    "C:a\\\\b",
    "c:/a/b/",
    "C:\\a\\..\\b",
    "C:/../a",
    "C:\\\\\\\\a",
    "C:\\a\\.\\b\\..\\..\\..\\c",
    "\\a\\b",
    "/a/b",
    "\\",
    "//x",
    "///x",
    "\\\\\\\\x",
    "\\\\server",
    "\\\\server\\",
    "\\\\server\\share",
    "\\\\server\\share\\",
    "\\\\server\\share\\db.sqlite",
    "\\\\server\\share\\a\\..\\b\\",
    "//server/share/x/y.cfg",
    "\\\\\\server\\share",
    "\\\\?\\",
    "\\\\?\\C:\\long\\path",
    "\\\\?\\C:\\long\\..\\path",
    "\\\\?\\UNC\\server\\share",
    "\\\\?\\UNC\\server\\share\\",
    "\\\\?\\UNC\\server\\share\\x\\db.sqlite",
    "\\\\?\\unc\\server\\share\\x",
    "\\\\?\\Unc\\server",
    "\\\\.\\NUL",
    "\\\\.\\C:\\x",
    "\\\\?\\GLOBALROOT\\Device\\x",
    "\U0001f600:\\x",
    "\U0001f600:x",
    "\U0001f600\\x",
    "é:\\x",
    "C:\\Users\\Ünïcode\\ファイル.txt",
    "C:\\a b\\c.d.e",
    "C:\\a\\b.",
    "C:\\a\\.hidden",
    "C:\\a\\b\\..",
    "C:\\..\\..\\a",
    "\\..\\a",
    "..\\..\\a",
    "C:..\\a",
]

JOINS = [
    ("C:\\a", "b"),
    ("C:\\a\\", "b"),
    ("C:\\a", "\\b"),
    ("C:\\a", "D:b"),
    ("C:\\a", "D:\\b"),
    ("C:\\a", "c:b"),
    ("C:\\a", "C:b"),
    ("C:", "b"),
    ("C:a", "b"),
    ("\\\\server\\share", "x"),
    ("\\\\server\\share\\", "x"),
    ("\\\\server\\share\\a", "\\x"),
    ("\\\\server\\share\\a", "\\\\other\\share\\y"),
    ("\\\\?\\UNC\\server\\share", "x"),
    ("a", "b"),
    ("a\\", "b"),
    ("a/", "b"),
    ("", "b"),
    ("a", ""),
    ("", ""),
    ("C:\\a", "/b"),
    ("\\\\?\\C:\\a", "b"),
    ("out", "/abs/x.json"),
    ("out", "nested/cap-snapshot.json"),
    ("C:\\Work", "\\rooted\\x"),
    ("\\\\?\\C:\\", "pagefile.sys"),
    ("\U0001f600:\\x", "y"),
    ("C:\\x", "\U0001f600:y"),
]

PURE = [
    ("\\\\server\\share\\db.sqlite", "\\\\server\\share"),
    ("\\\\server\\share\\db.sqlite", "\\\\server\\share\\"),
    ("\\\\server\\share\\a\\b", "//SERVER/share/A"),
    ("\\\\server\\share", "\\\\server"),
    ("\\\\server\\share\\", "\\\\server\\share"),
    ("\\\\?\\UNC\\server\\share\\x\\db.sqlite", "\\\\?\\UNC\\server\\share"),
    ("\\\\?\\UNC\\server\\share\\x\\db.sqlite", "\\\\server\\share"),
    ("\\\\?\\C:\\long\\path\\x", "\\\\?\\C:\\long"),
    ("C:\\a\\b\\c", "c:\\A"),
    ("C:\\a\\b\\c", "C:\\a\\b\\c"),
    ("C:\\a", "D:\\"),
    ("C:\\a", "C:"),
    ("C:a\\b", "C:a"),
    ("C:a\\b", "a"),
    ("a\\b\\c", "a"),
    ("a\\b\\c", "a\\b\\c\\d"),
    ("a/b/c/", "a/b"),
    ("\\a\\b", "\\a"),
    ("\\a\\b", "a"),
    ("/a/b", "\\"),
    ("C:\\", "C:\\"),
    ("C:", ""),
    ("", ""),
    (".", "."),
    ("a\\..\\b", "a"),
    ("\\\\.\\NUL", "\\\\.\\NUL"),
    ("C:\\a b\\c.d.e", "C:\\a b"),
    ("\U0001f600:\\x\\y", "\U0001f600:\\x"),
    ("\U0001f600:\\x\\y", "\U0001f600:\\"),
    ("C:\\Users\\Ünïcode\\ファイル.txt", "c:\\users\\ünïcode"),
    ("\\\\server\\share\\x", "\\\\?\\UNC\\server\\share"),
    ("C:\\a\\b", "\\\\?\\C:\\a"),
]

MATCH = [
    ("C:\\a\\B.txt", "*.TXT"),
    ("C:\\a\\B.txt", "a/*.txt"),
    ("C:\\a\\B.txt", "C:/a/*.txt"),
    ("C:\\a\\B.txt", "c:/A/b.TXT"),
    ("C:\\a\\B.txt", "/a/*.txt"),
    ("C:\\a\\B.txt", "D:/a/*.txt"),
    ("\\\\server\\share\\x.cfg", "//server/share/*.cfg"),
    ("\\\\server\\share\\x.cfg", "//SERVER/SHARE/*.cfg"),
    ("\\\\server\\share\\x.cfg", "*.cfg"),
    ("\\\\server\\share\\x.cfg", "share/*.cfg"),
    ("a\\b", "A/B"),
    ("a\\b", "a"),
    ("a\\b", "**/b"),
    ("a\\b\\c", "*/c"),
    ("a\\b\\c", "a/*"),
    ("a\\b\\c", ""),
    ("C:\\a\\[x]\\y", "[[]x]/y"),
    ("C:\\a\\.hidden", "*"),
    ("C:\\a\\b", "**"),
    ("\U0001f600:\\x", "x"),
    ("C:\\a\\ß.txt", "SS.txt"),
    ("C:\\a\\ẞ.txt", "ß.txt"),
    ("C:\\a\\K.txt", "\u212a.txt"),
]


def ntpath_cases() -> list[dict[str, object]]:
    return [
        {
            "path": path,
            "splitroot": list(ntpath.splitroot(path)),
            "splitdrive": list(ntpath.splitdrive(path)),
            "isabs": ntpath.isabs(path),
            "normpath": ntpath.normpath(path),
            "split": list(ntpath.split(path)),
        }
        for path in PATHS
    ]


def pure_windows() -> list[dict[str, object]]:
    cases = []
    for path, other in PURE:
        pure = PureWindowsPath(path)
        try:
            relative = pure.relative_to(other).as_posix()
        except ValueError:
            relative = None
        cases.append(
            {
                "path": path,
                "other": other,
                "parts": list(pure.parts),
                "str": str(pure),
                "parent": str(pure.parent),
                "anchor": pure.anchor,
                "is_absolute": pure.is_absolute(),
                "relative_to": relative,
                "joined": str(pure / other),
            }
        )
    return cases


def matches() -> list[dict[str, object]]:
    cases = []
    for path, pattern in MATCH:
        try:
            cases.append({"path": path, "pattern": pattern, "result": PureWindowsPath(path).match(pattern)})
        except ValueError as exc:
            cases.append({"path": path, "pattern": pattern, "error": str(exc)})
    return cases


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    data = {
        "ntpath": ntpath_cases(),
        "join": [{"path": a, "other": b, "result": ntpath.join(a, b)} for a, b in JOINS],
        "pure_windows": pure_windows(),
        "match": matches(),
    }
    path = OUT / "python_ntpath_cases.json"
    path.write_text(json.dumps(data, ensure_ascii=True, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {path}")


if __name__ == "__main__":
    main()
