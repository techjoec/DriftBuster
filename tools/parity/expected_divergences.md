# Expected parity divergences

The plan approves six behaviour fixes in the C# port. Each is a deliberate difference from
the Python oracle, so `run_parity.sh` must normalise it instead of failing on it. The
table lists the fix, the surface it shows up on, and how the harness handles it.

| Fix | Python behaviour | Port behaviour | Surface | Harness handling |
|---|---|---|---|---|
| a. config-id collision | Two apps sharing a file name collide on one config id | Ids include the app scope | multi-server | Ids are mapped (Python id -> C# id) by file path before comparing, never dropped |
| b. one unreadable file fails a host | Host scan aborts on the first unreadable file | File is skipped and counted | multi-server | Python output for the host is compared only up to the failing file; the C# record for the unreadable file is asserted to carry the skip counter |
| c. strict catalog validation rejects valid plugin output | `MetadataValidationError` for plist, markdown front matter, logstash and every HCL variant | Catalog carries those classes; the match succeeds | detect | `py_dump.py` swallows the rejection (a seam over `validate_detection_metadata`) so `scan_file` completes, and emits `"error": "MetadataValidationError: ..."` next to the plugin's own match fields; the harness drops the error from the Python record and the `catalog_*` keys from the C# record, compares the rest, and counts the path as an expected divergence |
| d. XML namespace prefix rewriting | Prefixes rewritten to `ns0`, `ns1`, ... | Prefixes kept as written | diff | Diff lines are normalised with a prefix map before comparing so the remaining diff stays meaningful |
| e. double-escaped install-path hunt regex | Regex never matches | Regex matches install paths | hunt | Python is run with the corrected pattern injected; the rule id is otherwise compared as usual |
| f. diff planner extension allowlist | Content type from the file extension | Content type from detection everywhere | diff | Planner records are compared on the detected type; the allowlist field is ignored |

### Fix c cases on the detect surface

| Case | Python | Port |
|---|---|---|
| `tools/parity/cases/detect/plugins-a/consul.hcl` | `MetadataValidationError: Unknown catalog variant 'hashicorp-consul' for format 'ini'` (the Python catalog files HCL under the ini format class) | `hcl` / `hcl` / `hashicorp-consul`, confidence 0.95, `blocks_preview: ["server"]` |
| `tools/parity/cases/detect/plugins-a/job.nomad.hcl` | `MetadataValidationError: Unknown catalog variant 'hashicorp-nomad' for format 'ini'` | `hcl` / `hcl` / `hashicorp-nomad`, confidence 0.95, `blocks_preview: ["job"]` |
| `tools/parity/cases/detect/plugins-a/logstash.conf` | `MetadataValidationError: Unknown catalog variant 'logstash-pipeline' for format 'unix-conf'` | `conf` / `unix-conf` / `logstash-pipeline`, confidence 0.8 |

The plugin output itself is identical on both sides (the Python plugin returns the same match before the
catalog rejects it); only the catalog enrichment differs, which is the approved fix. The harness proves the
first half of that sentence on every run: plugin, format, variant, confidence, reasons and every non-`catalog_`
metadata key are diffed for these paths like any other.

## CPython limits inside `json.loads` (not port defects)

| Case | Python behaviour | Port behaviour | Harness handling |
|---|---|---|---|
| `tools/parity/cases/detect/json-ini/big-int-5000.json` (`[` + 5000 digits + `]`) | `ValueError: Exceeds the limit (4300 digits) for integer string conversion` escapes the plugin (it catches only `JSONDecodeError`) and `Detector.scan_file` | `json` / `json` / `generic`, confidence 0.95, `top_level_sample_types: ["int"]`: the scanner classifies the literal without converting it | `py_dump.py` emits `{"path", "error": "InterpreterLimit: ValueError: ..."}`; the harness drops the path from both sides, asserts the port record is a successful `json` match, and counts it as an expected divergence |
| `tools/parity/cases/detect/json-ini/deep-nesting-20000.json` (20000 nested arrays) | `RecursionError` from the recursive decoder (5000 levels still parse) | `json` / `json` / `generic`, confidence 0.95, `top_level_sample_types: ["list"]`: the scanner keeps nesting on an explicit stack | Same handling, `InterpreterLimit: RecursionError: ...` |

Reproducing an interpreter crash is not a behaviour worth porting; the port parses both inputs.

## Match timeouts (port safety net)

Every .NET regex in the port carries a 2 s match timeout; Python has none. `Detector.ScanFile` reports a
`RegexMatchTimeoutException` from a plugin through `HandleError` as a `DetectorIOException` (the file is
recorded like an unreadable one, and a tolerant handler keeps the walk going) instead of aborting the scan.

The one pattern shape that can reach the timeout inside a default-size sample is Python's `^\s*...` under
MULTILINE: `\s*` crosses newlines, so a `^`-anchored search over a run of blank lines re-scans and backtracks the
run from every line start (quadratic; Python takes the same time, the port timed out). No phase 2 plugin searches
that shape with a `^`-anchored .NET regex any more: every such pattern (ini sections and key/value pairs, conf
Logstash blocks, toml table headers, array-of-tables and key = value, hcl key = value) is spelled with `\G` and
driven from each line start by `Infrastructure/LineStartMatcher.cs`, which skips a failed whitespace run once, and
every hand-rolled scanner of the same shape (dockerfile directives, hcl blocks, conf nested stanzas, ini Apache and
nginx hints, the yaml key/marker/comment scanners) advances the same way. `LineStartMatcherTests` proves the driver's
match set equals the `^`-anchored .NET regex's on adversarial inputs for every pattern each plugin hands it, and each
plugin's `WhitespaceRunsAreScannedInLinearTime` test bounds 50000 blank lines and 30000 space-only lines under 2 s.
`tools/parity/cases/detect/r2/blank-30000-then-section.ini` and the 5000-blank-line cases under `json-ini/` are
compared like any other file (toml/generic/0.65 on both sides under the full registry; the ini plugin alone yields
sectioned-ini 0.825, which is what `IniPluginTests` asserts).

## Lone surrogates in dump output (harness encoding, not a divergence)

A JSON key such as `"\ud83d"` decodes to an unpaired surrogate on both sides and both plugins keep it in
`top_level_keys`. Python cannot write it to a UTF-8 stdout (`UnicodeEncodeError`), .NET's console would replace it
with U+FFFD, and jq rejects the JSON escape of a lone surrogate. Both dumps therefore rewrite every unpaired surrogate
in every string (keys included) to the six characters `\uXXXX` (lower-case hex) before serialising, so the records
compare byte-for-byte (`py_dump._escape_lone_surrogates`, `CanonicalJson.EscapeLoneSurrogates`). Cases:
`tools/parity/cases/detect/r2/json-lone-surrogate-key.json`, `json-key-escapes.json`. A record the Python dump
still cannot serialise is emitted as `{"path", "error": "DumpError: ..."}`, and `run_parity.sh` fails that compare
(as it does a dump that exits non-zero or prints invalid JSON) and carries on with the next.

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
- `PluginGuardTests.PluginsReturnNoneWhenTextIsNone` covers every plugin ported so far (the seven Python
  checks minus registry_live, plus ini and json); registry_live joins the array when phase 3 ports it.
- `DetectorTests.ScanWithProfilesAttachesMatches` and
  `DetectorIgnoreExceptionTests.ScanWithProfilesIgnoreException` run the real registry as the Python tests
  do, and drive `IProfileMatcher` directly until phase 6 ports the profile store.

Phases 1 and 2 exercise only `detect` and `decode`, so only fix c, the two interpreter-limit cases and
the unreadable-root case can appear. `detect` without `--plugins` runs the Python side over `py_dump.PORTED_PLUGINS` (the set the
port has registered) so first-match-wins is comparable; the C# side runs its whole registry. Any
other difference is a port defect.
