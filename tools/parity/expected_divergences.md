# Expected parity divergences

The plan approves six behaviour fixes in the C# port. Each is a deliberate difference from
the Python oracle, so `run_parity.sh` must normalise it instead of failing on it. The
table lists the fix, the surface it shows up on, and how the harness handles it.

| Fix | Python behaviour | Port behaviour | Surface | Harness handling |
|---|---|---|---|---|
| a. config-id collision | Two apps sharing a file name collide on one config id | Ids include the app scope | multi-server | Ids are mapped (Python id -> C# id) by file path before comparing, never dropped |
| b. one unreadable file fails a host | Host scan aborts on the first unreadable file | File is skipped and counted | multi-server | Python output for the host is compared only up to the failing file; the C# record for the unreadable file is asserted to carry the skip counter |
| c. strict catalog validation rejects valid plugin output | `MetadataValidationError` for plist, markdown front matter, logstash and every HCL variant | Catalog carries those classes; the match succeeds | detect | `py_dump.py` emits `{"path", "error": "MetadataValidationError: ..."}`; the harness drops that path from both sides and reports it as an expected divergence |
| d. XML namespace prefix rewriting | Prefixes rewritten to `ns0`, `ns1`, ... | Prefixes kept as written | diff | Diff lines are normalised with a prefix map before comparing so the remaining diff stays meaningful |
| e. double-escaped install-path hunt regex | Regex never matches | Regex matches install paths | hunt | Python is run with the corrected pattern injected; the rule id is otherwise compared as usual |
| f. diff planner extension allowlist | Content type from the file extension | Content type from detection everywhere | diff | Planner records are compared on the detected type; the allowlist field is ignored |

## Detector walk decisions (plan "Windows semantics" 3 and 4)

| Case | Python behaviour | Port behaviour | Harness handling |
|---|---|---|---|
| Unreadable directory as the scan root | `Path.glob` swallows the `PermissionError`; `scan_path` returns `[]` with no error | `DetectorIOException` through `HandleError` (a root that cannot be read is an error, decision 4) | `run_parity.sh` builds a mode-000 root and asserts the Python dump is empty while the port emits exactly `{"error": "DetectorIOError", "path": "."}`; counted as an expected divergence |
| Walk order on Windows | `sorted(Path)` compares case-folded components on Windows | Components compared case-sensitively by code point on every platform (the posix order) | Not observable: the harness runs on Linux, where both sides agree |

Every other walk behaviour is compared directly: both dumps enumerate with their engine's own
`scan_path` (dangling symlink skipped, symlinked directory not followed, symlinked file scanned,
unreadable subdirectory skipped, unreadable file recorded as `{"path", "error": "DetectorIOError"}`,
missing or dangling root recorded with path `.`), scan with one detector so the
`--max-total-sample-bytes` budget stops the walk on the same file, and order records as the
engine yields them.

## Runtime Unicode tables (not port defects)

| Case | Python behaviour | Port behaviour | Harness handling |
|---|---|---|---|
| Code points assigned in Unicode 16.0 (U+1C89-U+1C8A, U+A7CB-U+A7CD, U+105C0-U+105F3, U+10D40-U+10D85, U+11BC0-U+11BF9, U+13460-U+143FA, U+16D40-U+16D79, U+1E5D0-U+1E5FA, ...) | Python 3.13 carries Unicode 15.1, so `\w`, `str.isalpha()` and `str.isalnum()` do not class them as letters or digits; a directive token containing one is "other" | .NET 10 carries Unicode 16.0, so `PythonText.IsWordRune` and `Rune.IsLetter` class them as word characters; the same token is a directive | No case file uses them: the difference is the runtimes' Unicode tables, not the port's character classes (`IsSpace`, line boundaries and the upper-case mapping are identical over every code point; there are no Python-only word characters) |

## Test mirror notes (not parity divergences)

- `TypesTests.ValidateDetectionMetadataRejectsBadMetadataType` and
  `ValidateDetectionMetadataRequiresStringVariant` assert the compile-time contract (an ordered
  string-keyed metadata dictionary, a string variant) instead of a runtime
  `MetadataValidationError`, which the typed port cannot raise for those inputs.
- `FormatRegistryTests.DecodeTextPrefersUtf8SigAndFallbackReplace` reaches the replace-mode latin-1 line
  through the internal `TextDecoder` seam (the port's `FussyBytes`), since real bytes never fail latin-1.
- `PluginGuardTests.PluginsReturnNoneWhenTextIsNone` covers the plugins ported so far; the
  Python test checks seven, and the array grows as phases 2 and 3 land the other six.
- `DetectorTests.ScanWithProfilesAttachesMatches` and
  `DetectorIgnoreExceptionTests.ScanWithProfilesIgnoreException` use stub JSON and YAML plugins
  until phase 2 ports the real ones, and drive `IProfileMatcher` directly until phase 6 ports the
  profile store.

Phase 1 exercises only `detect` and `decode`, so only fix c and the unreadable-root case can
appear. Any other difference is a port defect.
