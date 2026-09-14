# Expected parity divergences

The plan and the operator approve seven behaviour fixes in the C# port. Each is a deliberate difference from
the Python oracle, so `run_parity.sh` must normalise it instead of failing on it. The
table lists the fix, the surface it shows up on, and how the harness handles it. The sections after it record the
remaining differences and the basis for each: a numbered plan decision ("Windows semantics decisions"), a CPython interpreter
limit or interpreter-specific value that is not worth reproducing, or a difference between the runtimes' Unicode tables.

| Fix | Python behaviour | Port behaviour | Surface | Harness handling |
|---|---|---|---|---|
| a. config-id collision | The id comes from the first non-blank of `config_original_filename`, `config_role`, `msbuild_kind`, `top_level_type`, then the relative path, so two apps sharing `web.config`, or two JSON objects on one host, share an id and the later file replaces the earlier one | `slug(format)/slug(variant)/slug(relative posix path)`; a second record with an id its host already holds gets `@root{index}` (zero-based position of its root in the plan's roots, missing roots counted), then `.{n}` from 2 | multi-server | The fixed Python run replaces `_normalise_config_id` with the port's rule and must equal the port byte for byte; `multi_server_stock.py` diffs the fix b run against it, and every entry a collision explains is an expected divergence (details below) |
| b. one unreadable file fails a host | Host scan aborts on the first unreadable file (the hunt raises `PermissionError`, the host is `offline`; the detector raises `DetectorIOError`, the host is `permission_denied`); `hunt_path` raises on the first unreadable file (and on a file whose `is_file()` raises); a root directory that cannot be listed scans as empty and succeeds | File is skipped and counted (`HuntScanResult.UnreadableFiles`, `PlanScan.SkippedFiles`); only a root directory that cannot be listed or a root file that cannot be read fails the host with `permission_denied` | multi-server, hunt | multi-server: the fixed Python run makes the hunt and the detector skip such files and raise `DetectorIOError` for such a root, and must equal the port byte for byte; the stock run is diffed against it by `multi_server_stock.py` and every entry the skipped file or refused root explains is an expected divergence (details below). hunt: `run_parity.sh hunt` builds a tree with a file inside a directory that can be listed but not searched and asserts the Python dump ends with that file's `PermissionError` while the port lists exactly that file in `unreadable_files` and reports the readable file's hits; counted as an expected divergence. Any other compare fails on an `unreadable_files` record |
| c. strict catalog validation rejects valid plugin output | `MetadataValidationError` for plist, markdown front matter, logstash and every HCL variant | Catalog carries those classes; the match succeeds | detect | `py_dump.py` swallows the rejection (a seam over `validate_detection_metadata`) so `scan_file` completes, and emits `"error": "MetadataValidationError: ..."` next to the plugin's own match fields; the harness drops the error from the Python record and the `catalog_*` keys from the C# record, compares the rest, and counts the path as an expected divergence |
| d. XML namespace prefix rewriting | Prefixes rewritten to `ns0`, `ns1`, ... | Prefixes kept as written | diff, canon, multi-server | Canonical texts: Python's prefixes are mapped back to the source prefixes and every `xmlns` declaration is dropped on both sides. Everything after canonicalisation on `diff` is recomputed by CPython from the port's canonical texts and compared exactly (details below) |
| e. double-escaped install-path hunt regex | Regex never matches a Windows path | Regex matches install paths | hunt | The Python dump runs with the corrected pattern injected (`PARITY_HUNT_FIX_E=1`) and every record is compared exactly; a stock Python dump must equal it on every record whose rule is not `install-path`, the differing `install-path` lines are counted as expected divergences, and for `tools/parity/cases/hunt` the port is asserted to find the intended Windows path where stock Python does not (details below) |
| g. redaction loop that never returns | `copy_with_secret_filter` repeats forever on a line where rules keep matching inside the `[SECRET]` text they inserted | Past a fixed budget of such replacements the line goes back to its last replacement that consumed source text and the looping rules are stopped on it | secrets | `py_dump.py` gives each file `PARITY_SECRETS_TIMEOUT` seconds; `secrets_guard.py` replaces a file Python never finished with its reference model's record (CPython `re`, the same rule), which must carry a guard, and the port is compared exactly (details below) |
| f. diff planner extension allowlist | The GUI planner and `cli.py` choose xml by file extension; multi-server chooses from detection | `Diff/ContentTypeResolver`: detection everywhere, with multi-server's rule (`xml` for catalog format `structured-config-xml` or `xml`, otherwise `text`) | diff | No divergence on the harness: the Python planner is not reachable from `py_dump.py`, which resolves the default content type with the same rule over the Python detector, so records are compared exactly |

### Fix c cases on the detect surface

| Case | Python | Port |
|---|---|---|
| `tools/parity/cases/detect/plugins-a/consul.hcl` | `MetadataValidationError: Unknown catalog variant 'hashicorp-consul' for format 'ini'` (the Python catalog files HCL under the ini format class) | `hcl` / `hcl` / `hashicorp-consul`, confidence 0.95, `blocks_preview: ["server"]` |
| `tools/parity/cases/detect/plugins-a/job.nomad.hcl` | `MetadataValidationError: Unknown catalog variant 'hashicorp-nomad' for format 'ini'` | `hcl` / `hcl` / `hashicorp-nomad`, confidence 0.95, `blocks_preview: ["job"]` |
| `tools/parity/cases/detect/plugins-a/logstash.conf` | `MetadataValidationError: Unknown catalog variant 'logstash-pipeline' for format 'unix-conf'` | `conf` / `unix-conf` / `logstash-pipeline`, confidence 0.8 |
| `fixtures/binary/preferences.plist`, `tools/parity/cases/detect/plugins-b/{preferences,array-top,short,truncated}.bplist` | `MetadataValidationError: Unknown catalog format: plist` | `binary-hybrid` / `plist` / `xml-or-binary`, confidence 0.92; `top_level_keys` sorted by code point, or `decode_error` |
| `tools/parity/cases/detect/plugins-b/front-matter-{only,crlf,bom,x1c}.md` | `MetadataValidationError: Unknown catalog format: markdown-config` | `binary-hybrid` / `markdown-config` / `embedded-yaml-frontmatter`, confidence 0.8 |
| `tools/parity/cases/detect/r2/conf-logstash-*.conf`, `yaml-toml-misc/logstash-x1c.conf`, `r2/hcl-*.hcl`, `yaml-toml-misc/hcl-*` | The same two catalog rejections as the plugins-a HCL and logstash cases | Same as above |

The plugin output itself is identical on both sides (the Python plugin returns the same match before the
catalog rejects it); only the catalog enrichment differs, which is the approved fix. The harness proves the
first half of that sentence on every run: plugin, format, variant, confidence, reasons and every non-`catalog_`
metadata key are diffed for these paths like any other.

## CPython limits inside `json.loads` (not port defects)

| Case | Python behaviour | Port behaviour | Harness handling |
|---|---|---|---|
| `tools/parity/cases/detect/json-ini/big-int-5000.json` (`[` + 5000 digits + `]`) | `ValueError: Exceeds the limit (4300 digits) for integer string conversion` escapes the plugin (it catches only `JSONDecodeError`) and `Detector.scan_file` | `json` / `json` / `generic`, confidence 0.95, `top_level_sample_types: ["int"]`: the scanner classifies the literal without converting it | `py_dump.py` emits `{"path", "error": "InterpreterLimit: ValueError: ..."}`; the harness drops the path from both sides, asserts the port record is a successful `json` match, and counts it as an expected divergence |
| `tools/parity/cases/detect/json-ini/deep-nesting-20000.json` (20000 nested arrays) | `RecursionError` from the recursive decoder (5000 levels still parse) | `json` / `json` / `generic`, confidence 0.95, `top_level_sample_types: ["list"]`: the scanner keeps nesting on an explicit stack | Same handling, `InterpreterLimit: RecursionError: ...` |
| `tools/parity/cases/detect/binary-regl-r1/scan-int-limit.json`, `v1-regscan-bigint-4301.txt`, `v1-regscan-deep-keywords-12000.txt` (a `registry_scan` manifest past the same two limits) | registry-live's `json.loads` raises, the plugin catches it and returns None; the json plugin then raises the same error uncaught | registry-live refuses the manifest as Python does (`PythonJson` fails past 4300 digits and past 9998 nested containers); the json plugin's scanner matches | Same handling |

Reproducing an interpreter crash is not a behaviour worth porting; the port parses both inputs in the json
plugin. Inside registry-live the limits are reproduced, because there Python survives them and returns None.

registry-live copies `max_depth`, `max_hits` and `time_budget_s` into metadata as they are, so a nested list there
reaches `validate_detection_metadata`. Python's `_json_safe` recurses once per level: past the interpreter's frame
limit (993 nested lists pass under `py_dump.py`, 994 raise) the `RecursionError` escapes `Detector.scan_file`, which
`py_dump.py` records as `InterpreterLimit: RecursionError`. The port converts metadata on an explicit stack and returns
the registry-live match (up to the 9998-container decoder limit, past which both sides refuse the manifest). This is
the same interpreter-limit class as above and is not reproduced. No case covers it: from about 254 levels any record
carrying the nesting is deeper than jq 1.7 parses (256 levels), on either side, so the harness would fail the compare
as "dump is not valid JSON lines". Keyword and pattern lists are stringified before they reach metadata, so they nest
nothing; their decoder boundary (9995 nested lists accepted, 9996 refused) is identical on both sides.

## Match timeouts (port safety net)

Every .NET regex in the port carries a 2 s match timeout; Python has none. The Python `re` port (`Infrastructure/PythonRe`) is
not a .NET regex and has no timeout (see "Python regular expressions"). `Detector.ScanFile` reports a
`RegexMatchTimeoutException` from a plugin through `HandleError` as a `DetectorIOException` (the file is
recorded like an unreadable one, and a tolerant handler keeps the walk going) instead of aborting the scan.

The one pattern shape that can reach the timeout inside a default-size sample is Python's `^\s*...` under
MULTILINE: `\s*` crosses newlines, so a `^`-anchored search over a run of blank lines re-scans and backtracks the
run from every line start (quadratic; Python takes the same time, the port timed out). No plugin searches
that shape with a `^`-anchored .NET regex: every such pattern (ini sections and key/value pairs, conf
Logstash blocks, toml table headers, array-of-tables and key = value, hcl key = value, the registry-live YAML
key, token, keywords and patterns lines) is spelled with `\G` and
driven from each line start by `Infrastructure/LineStartMatcher.cs`, which skips a failed whitespace run once, and
every hand-rolled scanner of the same shape (dockerfile directives, hcl blocks, conf nested stanzas, ini Apache and
nginx hints, the yaml key/marker/comment scanners, the xml generic-element probe `^\s*<[^!?][\w:.-]+(\s|>)`)
advances the same way. binary-hybrid's front matter pattern `^---\s*\n(?P<block>.*?\n)---\s*\n` (DOTALL) is
quadratic in Python on an opening fence followed by blank lines and no closer; the port matches it in one pass
(`BinaryHybridPlugin.TryMatchFrontMatter`), so `binary-regl-r1/v1-fm-open-7000-blank.md` is compared like any other file. `LineStartMatcherTests` proves the driver's
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
| A FIFO, socket or device (or a link to one) under the root | `Path.is_file()` is False: skipped, never opened | `PythonPath.IsFile` is a `stat` of the file type: skipped, never opened (see "Platform limits") | `run_parity.sh` adds a FIFO, a socket and a `/dev/null` link to the generated trees of detect, decode, hunt and secrets |
| A file inside a directory that can be listed but not searched (mode 0644) | `Path.is_file()` raises `PermissionError` (it ignores only `ENOENT`, `ENOTDIR`, `EBADF` and `ELOOP`): `scan_path` hands a `DetectorIOError` to `_handle_error`; `hunt_path` aborts | `PythonPath.IsFile` raises for every other `statx` error (`UnauthorizedAccessException` for `EACCES`): `Detector.ScanPath` reports it through `HandleError`; `HuntEngine.HuntPath` lists it as unreadable (fix b). The glob lists the directory's names without a `stat` per entry, as `os.scandir` does | detect, decode and secrets: both dumps list an entry the walk reported without scanning it, so the generated trees' `unsearchable-sub/refused.*` is compared as a `DetectorIOError` record; hunt: see fix b |
| A file root spelled with a trailing separator (`x.config/`) | `Path(root)` drops the separator and scans the file | The root is spelled with `PythonPurePath.Str` first, in `Detector.ScanPath`, `HuntEngine.HuntPath` and the dumps' walk | Compared: detect and decode `generated/file root with trailing separator`, hunt and secrets `... file root with trailing separator`, diff `tools/parity/cases/diff pair with trailing separators` (both dumps spell each pair path as `Path()` does) |
| A `..` part after a symlink (`link/../x`) | The kernel steps to the parent of wherever `link` leads | .NET removes `..` lexically before every file-system call, so every call the walks, the detector, the hunt, the secret copy, the SQLite count and the multi-server runner make goes through `PythonPath.KernelPath`, which resolves every part up to the last `..` physically and appends the rest as written; enumerated paths keep their `..` (`PythonPath.Absolute`, not `Path.GetFullPath`), and a path compared the way `os.path.abspath` compares it is still normalised lexically | Compared, no divergence: detect and decode `generated/root with .. after a symlink`, hunt and secrets `generated/... root with .. after a symlink`, multi-server `algorithm-r1/dotdot-through-symlink`, `runtime-r1/dotdot-through-symlink` and `runtime-r1/secrets-link-and-dotdot` |
| Walk order on Windows | `sorted(Path)` compares case-folded components on Windows | Components compared case-sensitively by code point on every platform (the posix order) | Not observable: the harness runs on Linux, where both sides agree |

Both walks are `sorted(root.glob(glob))`; the port runs a port of `Path.glob` (`Infrastructure/PythonGlob`), so a custom glob
follows a symlinked directory exactly where Python does (a wildcard part) and the default `**/*` never does.
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
| The same code points in a multi-server config id | `_slugify` keeps only `str.isalnum()` code points, so `Ᲊ.ini` slugs to `--ini` | Not a divergence: `PythonText.IsAlnum` excludes every L* or N* code point Python's tables leave unassigned (the Unicode 16.0 letters and numbers, listed in `PythonText`), so ids, their order and the `@root` suffixes match | Compared: multi-server `runtime-r1/unicode-names` and `runtime-r1/baseline-astral-fallback`; `PythonReOracleTests.IsAlnumRejectsEveryLetterAndNumberPythonsTablesLeaveUnassigned` checks every code point |
| The same code points inside `repr` (`tools/parity/cases/detect/binary-regl-r2/r2-regscan-repr-unicode16.json`) | registry-live stores `str(item)` for keyword and pattern items; `repr` escapes a code point `str.isprintable()` rejects, and under Unicode 15.1 every 16.0 assignment is unassigned, so a nested list holding U+0897 becomes `['\\u0897']` | `PythonRepr.IsPrintable` applies the same rule to .NET's 16.0 categories and keeps the character literal; over every non-surrogate code point the rules differ only on 16.0 assignments, all printable in .NET and escaped in Python | For a registry-live record from a case whose file name contains `unicode16`, `metadata.keywords` and `metadata.patterns` are dropped from both records and the rest is compared; counted as an expected divergence |

## Test mirror notes (not parity divergences)

- `TypesTests.ValidateDetectionMetadataRejectsBadMetadataType` and
  `ValidateDetectionMetadataRequiresStringVariant` assert the compile-time contract (an ordered
  string-keyed metadata dictionary, a string variant) instead of a runtime
  `MetadataValidationError`, which the typed port cannot raise for those inputs.
- `FormatRegistryTests.DecodeTextPrefersUtf8SigAndFallbackReplace` reaches the replace-mode latin-1 line
  through the internal `TextDecoder` seam (the port's `FussyBytes`), since real bytes never fail latin-1.
- `PluginGuardTests.PluginsReturnNoneWhenTextIsNone` covers the seven Python checks (yaml, toml, text,
  dockerfile, hcl, conf, registry_live) plus ini and json; binary-hybrid is absent on both sides because it
  accepts `text=None` (it re-derives the text from the sample).
- `DetectorTests.ScanWithProfilesAttachesMatches` and
  `DetectorIgnoreExceptionTests.ScanWithProfilesIgnoreException` run the real registry as the Python tests
  do, and drive `IProfileMatcher` directly until phase 6 ports the profile store.

## Binary-hybrid values read from the real file (compared, not divergences)

- `table_count` on a SQLite match is read from the file on both sides (`sqlite3` in read-only URI mode; `Microsoft.Data.Sqlite`
  in read-only mode through a connection-string builder), so the value is compared like any other key:
  `plugins-b/settings.sqlite` yields 2 (two tables, a view and an index excluded) and `plugins-b/corrupt.sqlite` (a valid
  16-byte header over garbage) yields `null` with no "Enumerated" reason on both sides.
- `decode_error` on a binary plist is compared verbatim. `BinaryPlist` reproduces `plistlib._BinaryPlistParser`'s acceptance
  rules and its wrapping of every failure into `InvalidFileException("Invalid file")`: `plugins-b/truncated.bplist` (a valid
  payload less its last 20 bytes) and `plugins-b/short.bplist` (`bplist00` plus ten NUL bytes, shorter than the 32-byte trailer)
  both yield `{"type": "InvalidFileException", "message": "Invalid file"}` on both sides. No documented divergence is needed.
- A plist nested deeply enough records `decode_error` `RecursionError` in Python. `BinaryPlist` counts interpreter frames
  (`FrameLimit`, `ParseFrame`) from the depth `parse` runs at under `py_dump.py`, so the boundary is compared exactly:
  `binary-regl-r1/f3-bplist-{array,dict}-depth-{987,988}.bplist` and `f3-bplist-cached-leaf-depth-{988,989}.bplist` sit on
  either side of it. A Python caller with a different stack depth moves the boundary by its own frame count.

## SQLite file names read as URIs (plan decision 7)

| Case | Python behaviour | Port behaviour | Harness handling |
|---|---|---|---|
| `tools/parity/cases/detect/binary-regl-r1/v1-sqlite-pct%41.sqlite` | `sqlite3.connect(f"file:{path}?mode=ro", uri=True)` decodes `%41`, opens a file that does not exist and fails: `table_count` null, no "Enumerated" reason | The connection-string builder opens the real file: `table_count` 2 and "Enumerated 2 table(s) via sqlite3 pragma" | For a binary-hybrid SQLite record whose path holds `%XX`, `?` or `#`, `table_count` and the "Enumerated ..." reason are dropped from both records and the rest is compared; counted as an expected divergence |

Names containing `?` or `#` make Python open a truncated path read-write (creating a stray file), so no case uses them.

## Diff surface: canonicalisation and clamps

Fix d in detail. `canonicalise_xml` in Python serialises through `ET.tostring`, which renames every namespaced element
and attribute to `ns0`, `ns1`, ... in first-use order (or a well-known prefix such as `xsi` for its URI), declares every
used namespace once on the root sorted by that prefix, and drops unused declarations. The port (`Diff/Canonicaliser.Xml.cs`)
writes names with the prefixes as written and keeps each `xmlns` / `xmlns:p` declaration (including `xmlns=""` and ones the
DTD defaults) on the element that makes it, sorted by prefix and before the attributes. Attribute order is Python's on both
sides (by `{uri}local`).

Harness handling (`run_parity.sh`, `NS_DEFS`), on the `diff` and `canon` surfaces, for a record whose content type is xml and
whose canonical text on either side declares a namespace: each Python prefix is looked up in Python's root declarations to
get its URI, the URI is looked up in the port's declarations to get the prefix the source used (none for a default
namespace), and every Python element and attribute name is renamed to it; then every `xmlns` / `xmlns:p` attribute is
dropped from both sides and the canonical texts are compared. On `diff` this text compare applies to canonical texts neither
side clamped (a clamped text is cut at a byte offset that depends on the declarations; the same files are compared unclamped
on `canon`). The renamed text cannot stand in for the rest of a diff record: renaming changes which lines are equal (Python's
`ns0` for two URIs, the port's two source prefixes for one URI), so hunks, stats and line counts legitimately differ, and with
`--mask` the port's redundant declarations are redacted too. So for every such pair `recompute_fixd` reads the port's canonical
texts (before the clamps) with the port's `canon` surface and reruns `py_dump.py diff` with `PARITY_PORT_CANONICAL`, which makes
`build_unified_diff` take those texts in place of canonicalising: the redaction, difflib, stats, clamps, digests, summary and key
order are then CPython's own over the port's texts, and the port record is compared with that record exactly (digests
included). Cases: `tools/parity/cases/diff/diff-r1-close/fixd-prefix-rename-changes-line-equality`,
`fixd-declaration-value-spans-lines` (a namespaced document against a text fallback whose declaration value spans lines) and
`fixd-mask-counts-redundant-declaration`. Dropping declarations hides where they sit, so every xml record on
both surfaces is also checked by `tools/parity/xml_semantics.py`: each unnormalised canonical text is parsed on both sides
with namespace processing (after the verbatim XML declaration and DOCTYPE prolog) and the element trees are compared by
expanded name (`{uri}local` tags and attributes, text, tails, comments, child order). A declaration on the wrong element, a
missing `xmlns=""` undeclaration or an unbound prefix fails the compare; a text that parses on neither side (a text fallback
or a clamped payload) is left to the textual compare. The fix itself is asserted by the same script: for each namespaced record,
every port canonical text and the source document it came from (the canon root joined with the record path, or the diff pair's
file) are parsed without namespace processing, and the qualified names as written (element tags in document order, each with its
sorted non-`xmlns` attribute names) must be identical, so a port that rewrote a prefix fails even though the normaliser would
map it away. Each such record is counted as an
expected divergence: on `diff`, the pairs of `fixtures/multi-server/*/localization/Strings.resx` and
`tools/parity/cases/diff/xml-namespaced`; on `canon` as xml, the same files plus `fixtures/config/web.Release.config` and
`fixtures/xml/namespace_provenance_sample.xml`. The mapping assumes one prefix per URI within a document; no case binds
one URI to two prefixes.

| Case | Python behaviour | Port behaviour | Harness handling |
|---|---|---|---|
| XML whose DOCTYPE reaches the parser (not at the start, or the bracket scan cannot close it) and declares entities | Plain expat expands internal entities | Refused (no entity is ever resolved); canonicalised as text | DTD processing is Prohibit (plan settled scope); no harness case |
| XML nested past Python's recursion limit (about 990 levels) | `RecursionError` from `_normalise` / `_serialize_xml` escapes `build_unified_diff` | Normalised and serialised on an explicit stack | Interpreter limit, not reproduced |
| XML text holding an unpaired surrogate | `UnicodeEncodeError` from `XMLParser.feed` escapes | Parse failure, canonicalised as text | Interpreter error, not reproduced |
| JSON past `json.loads` limits (4300-digit integers, 9999 nested containers) | `ValueError` / `RecursionError` escapes (only `JSONDecodeError` is caught) | Falls back to `canonicalise_text` | Interpreter limit, not reproduced |
| Canonical payload or diff holding an unpaired surrogate | Strict UTF-8 encode in the clamps and digests raises `UnicodeEncodeError` | Encoded with U+FFFD for sizes and digests | Interpreter error, not reproduced. `py_dump.py` emits `"error": "InterpreterError: UnicodeEncodeError"` for a pair failing with "surrogates not allowed"; the harness does not compare that pair, asserts the port record is a successful result and counts it as an expected divergence (`diff-r1-close/json-lone-surrogate-key-vs-private-use`, whose key order is compared on `canon`) |
| `context_lines` outside the 32-bit range | `build_unified_diff` takes any `int`; past -2^31 or 2^31 - 1 the hunk headers print the unbounded values | `contextLines` is an `int` through `SequenceMatcher.GetGroupedOpcodes`, `UnifiedDiff.Lines`, `DiffBuilder.BuildUnifiedDiff` and `DiffArtifact.ContextLines` (the arithmetic on it is 64-bit, so every 32-bit value matches CPython); the parity dump rejects a larger `--context` as an argument error | API limit (a context of more than two billion lines has no use), not reproduced; no case passes one |

Both dumps serialise with sorted keys, so every diff record also carries `key_order`: the insertion order of every mapping in
`result` and `summary` as nested `[key, child]` lists (`py_dump._key_order`, `CanonicalJson.KeyOrder`). It is compared like any
other field, so `result.redaction_counts` in first-hit order, the summary's `redaction_counts` in token order, the
`safety_limits` order (`thresholds`, `canonical` with `before` then `after`, `diff`) and the payload's key order must match.

`gui/DriftBuster.Backend.Tests/Diff/Data/*.json` hold the CPython outputs the C# tests compare against (difflib opcodes,
matching blocks, grouped opcodes and unified diffs; the three canonicalisers; `build_unified_diff` results and summary
payloads, clamps included). Regenerate with `tools/parity/gen_diff_cases.py`.

## Python regular expressions (`Infrastructure/PythonRe`)

Hunt rules, secret rules, their ignore patterns and glob wildcards are arbitrary Python `re` patterns, so the port carries
CPython 3.13's regular expression stack: a port of `re._parser` (same syntax tree, errors and positions), of
`re._compiler._compile` (same opcode layout) and of `_sre`'s `SRE(match)` / `SRE(search)` (`ReMatcher`: the same opcode
semantics, backtracking order, mark stack, repeat contexts with their `last_ptr` zero-width guard, possessive and atomic
handling, `lastindex` bookkeeping and the `finditer` empty-match rule), run over the subject's code points. Single-character
opcodes are folded into code point sets built with `_sre`'s case tables. `\N{name}` resolves through
`Resources/unicode_names.txt.gz`, the interpreter's Unicode 15.1 name table and aliases (written by
`tools/parity/gen_unicode_names.py`), with Hangul syllable and CJK unified ideograph names computed as `unicodedata` computes
them. `gui/DriftBuster.Backend.Tests/Infrastructure/Data/python_regex_cases.json` holds the interpreter's case tables over
every code point, the code point set of each atom over every code point, `finditer` / `search` / `lastindex` results or compile
errors for the hunt and secret patterns plus adversarial ones (empty-body lazy repeats, possessive counted repeats,
case-insensitive back-references, named escapes), and `PurePosixPath.match` results. Regenerate with
`tools/parity/gen_regex_cases.py`.

Plan decision 10 asks for match timeouts on the XML snippet regexes and does not cover the `re` port: a `PythonPattern` search has no time limit, as in
Python: a slow search returns Python's result however long it takes, and hunt and secret output never depend on host speed.
`Match`, `Search` and `FindIter` take an optional `CancellationToken` that the matcher polls every 4096 opcodes (and
`HuntPath` forwards its token into every search and the directory walk), so a caller can abort instead. See "Hunt rule search
time" for the work the matcher reuses on long lines.

| Case | Python behaviour | Port behaviour | Basis |
|---|---|---|---|
| Group names | `str.isidentifier()` (XID_Start / XID_Continue with NFKC closure) | General categories behind ID_Start / ID_Continue | Runtime Unicode tables; a name spelled with one of the few NFKC-unstable code points compiles in the port only |
| A repeat count literal past 4300 digits | `ValueError` from `int()` | `OverflowException` ("the repetition number is too large") | Interpreter limit, not reproduced |

## Hunt and secret scanning

| Case | Python behaviour | Port behaviour | Harness handling |
|---|---|---|---|
| Fix e: install-path drive pattern | `r"[A-Za-z]:\\\\[\\w\\-\\.\\s]+"` needs two literal backslashes and never matches `C:\Program Files` | `[A-Za-z]:\\[\w\-\.\s]+`; the class holds no backslash, so the match (and plan transform value) for `C:\Program Files\Vendor` is `C:\Program Files` | Unit-test oracle: `gen_hunt_secret_cases.py` runs Python with the corrected pattern injected and every hunt record is compared exactly. `run_parity.sh hunt`, per compare: the Python dump runs with `PARITY_HUNT_FIX_E=1` (the same injection) and its records are diffed exactly against the port's; a second, stock Python dump over the same arguments must be identical to the fixed one once `install-path` records are removed from both (otherwise the compare fails with "fix e changed records other than install-path hits"); the expected divergence count is the number of `install-path` lines that differ between the stock and fixed dumps (a changed record counts once on each side). Only for the `tools/parity/cases/hunt` compare, `INSTALL_PATH_EXPECTATIONS` asserts that `install-windows-single.ini:2` (`C:\Program Files`) and `install-windows-mixed.txt:1` (`D:\Apps`) each have exactly one port `install-path` hit with that plan transform value and none in the stock dump |
| Fix b: a file that cannot be opened or read | `PermissionError` escapes `hunt_path`, aborting the hunt | Skipped and listed in `HuntScanResult.UnreadableFiles` (`HuntResult.unreadable_files` in the GUI model, omitted when empty) | Recorded; no harness case (the port dump's `unreadable_files` record would fail a compare) |
| Hunt root directory that exists but cannot be listed | `Path.glob` swallows the error, `[]` | The I/O error propagates | Plan decision 4 (raise only when a root is unreadable), as on the detect surface; no harness case |
| `placeholder_template` field with an attribute `str` has (`{token_name.upper}`) | Formats a bound method or type object, text carrying a memory address | `NotSupportedException` | Interpreter-specific text, not reproduced; every other `str.format` behaviour (conversions, format specs, indexing, nested fields, errors) is ported |
| `shutil.copystat` | Copies mode, times and (Linux) extended attributes and flags | Copies the Unix mode and access/modification times | Not compared (the oracle compares bytes, sizes and digests) |
| Packaged ruleset that is not valid JSON | `JSONDecodeError` | `InvalidDataException` | Both raise; the shipped resource is valid |

`gui/DriftBuster.Backend.Tests/Hunt/Data/hunt_cases.json` and `Secrets/Data/secret_cases.json` hold `hunt_path(...,
return_json=True)` over a generated tree (walk order, globs, excludes, sample size, file roots, custom rules with nested
groups, keyword-only and zero-width rules) and `copy_with_secret_filter` / `build_context` / `secret_option_values` /
`compile_ruleset_from_mapping` results. Redacted copies are written with platform newlines on both sides; the oracle is
Linux output, so the byte comparison of redacted copies runs on posix hosts.

Directory roots are walked with a port of CPython 3.13's `Path.glob` (`Infrastructure/PythonGlob`): the same selectors
(`**` without following symlinked directories, wildcard parts following them, literal parts joined without listing, `..` and
a trailing separator appended), `glob.translate` wildcards, pattern parsing and errors (`ValueError` for an empty pattern,
`NotImplementedError` for an anchored one) and duplicate results, then `sorted()`. A missing root yields no hits, as in
Python. `run_parity.sh hunt` compares character classes, `?`, nested wildcards, trailing separators, `//`, `..` parts and a
generated tree with a symlinked directory, a symlinked file and a dangling link.

The GUI facade `DriftbusterBackend.HuntAsync` delegates to `HuntEngine` with the seven default rules. It keeps its own
existing check that the root exists (`FileNotFoundException("Path does not exist: <root>")`). Its `pattern` argument is not a
Python concept and keeps its existing meaning: a case-insensitive substring filter on the excerpt.

### Fix g: redaction loop terminates where Python never returns

Python's `copy_with_secret_filter` searches the working line with every rule in order after each replacement and replaces the
first rule's first match with `[SECRET]`. When rules keep matching inside the `[SECRET]` text inserted on that line (`secret` with
flag `i` matches `SECRET`; `\u017f+` with flag `i` matches its `S`; `(?<!\[\[)SE` and `T\]` take turns inside each other's
replacement), the call never returns. The exact rule the port applies (`SecretScanner.CopyWithSecretFilter`), per line:

1. Every character of the working line is source text or inserted text (a character of a `[SECRET]` this loop wrote; text that
   was already `[SECRET]` in the source is source text). Replacements are made exactly as Python makes them, findings and log
   lines included.
2. A replacement whose match holds a source character consumes source text; that can happen only as often as the line has
   characters, so a line Python never leaves is one that, from some point on, replaces only inside inserted text, and (a
   shrinking replacement being bounded by the line length) does so without shrinking the line infinitely often.
3. The port counts non-empty, non-shrinking replacements lying wholly inside inserted text since the line's last replacement that
   consumed source text. When the next one would exceed `SecretScanner.GuardBudget` (1024), the line goes back to its state
   after that last replacement (the findings and log lines recorded since are dropped), every rule that replaced inside
   inserted text since then is stopped on that line, in the order it first did so, and recorded in
   `SecretDetectionContext.RedactionGuards` (rule and line), and the search carries on with the remaining rules.

The port therefore differs from Python only on a line where Python makes more than `GuardBudget` such replacements after its
last replacement that consumed source text: every line Python never returns from, and a line where a rule stops matching only
after that many (a look-behind or look-ahead wider than 1024 characters that counts nested brackets or the line's length).
`SecretScannerEdgeTests.TheGuardBudgetCountsReplacementsInsideInsertedText` pins the boundary: `(?<!\[{1025})SEC` returns
CPython's output with 1025 findings, `(?<!\[{1026})SEC` is guarded. The looping unit tests end with `token [SECRET]`,
`a[SECRET]b` and, for the two alternating rules, `[SECRET]C` with both rules guarded; `(?<!\[\[\[)SEC`, which Python
finishes after three replacements inside inserted text, returns CPython's `[[[SECRET]RET]RET]`.

Proof of the "only where Python never returns" half: `gen_hunt_secret_cases.py` adds `guard-overlap-adjacent-generated`
(24 seeded lines of overlapping, adjacent and placeholder-straddling matches of six rules) and
`guard-inside-placeholder-terminates` (look-behind, look-ahead and zero-width matches inside inserted placeholders on which
Python returns) to the unit-test oracle, where the port's output bytes, findings and log lines must equal CPython's. The same
inputs are `run_parity.sh secrets` cases under `tools/parity/cases/secrets-guard/<set>/{ruleset.json,input/}`, next to the
looping `self-matching`, `long-s` and `hunt-secrets-r1-close-alternating` sets and the terminating `hunt-secrets-r1-close` set.
Harness handling: `py_dump.py` gives each file `PARITY_SECRETS_TIMEOUT` seconds (5 by default, `SIGALRM`) and emits
`{"path", "error": "Timeout"}` for a copy that has not returned; the port dump adds `redaction_guard` to a record only when the
guard fired. `secrets_guard.py` runs the rule above with CPython's `re` over every enumerated file (the reference model) and
prints the Python dump with each timed-out record replaced by the model's record; a timed-out file on which the model fires no
guard, and a file Python returned on whose model record differs from Python's own, fail the compare. The port records are then
compared with that output exactly, `redaction_guard` included, and each replaced record is counted as an expected divergence.

## Multi-server surface (fixes a, b and d)

`run_parity.sh multi-server` runs every plan file under `tools/parity/cases/multi-server/`, at any depth outside `trees/`
directories (a request whose `plans` both dumps decode with `multi_server._build_plans`'s coercions, `MultiServerPlan.BuildPlans`
in the port, plus the harness keys `args`, `requires`, `fixes`, `stock_aborts`, `aborts`, `divergences` and `self_test`), through
`py_dump.py multi-server` and `driftbuster parity-dump multi-server`: `MultiServerRunner(cache, sample_budget,
sample_size).run(plans)` in process over a fresh cache (`--runs 2` dumps the second run against the first run's cache), printed
as one document `{progress, response, cache, key_order}`. Progress is every `{host_id, status, message}` the runner emitted;
`cache` is `{file, signature, sha256}` per cache file; every `timestamp`, `last_updated`, `last_seen` and `generated_at` is the
token `<timestamp>` when it is spelled exactly as `datetime.now(UTC).isoformat()` spells it (six fraction digits or none, then
`+00:00`; the port dump writes a model timestamp that way only when it has offset zero and microsecond precision), otherwise
it is kept inside `<invalid timestamp: ...>`; absolute roots are respelled relative to the working directory in
`results[].roots` and in each result message. The port side serialises the `ServerScanResponse` model itself, so its property
names, order and enum spellings are compared. Trees that git cannot hold (mode 000 files and roots, a root inside a directory
that cannot be searched, a directory that can be listed but not searched, FIFOs, sockets, links, undecodable names, a file past
the 128 KiB sample) are generated per run under `$PARITY_MULTI_SERVER_WORK` by `run_parity.sh`,
`cases/multi-server/runtime-r{1,2,3}/generate_trees.sh`, and both runners expand the variable in the plan roots. Both dumps
spell an unpaired surrogate in any string as the literal text `\uXXXX` (the port dump's model serialiser escapes it before the
runtime's writer would substitute U+FFFD).

A case with `aborts` holds a request every runner must abort on: all four dumps must exit non-zero, print nothing and carry that
text in their error output. `cases/multi-server/request-coercion/` covers the coercion errors `Plan.from_mapping` and
`_build_plans` raise (`int()` of a bad priority, iterating a number as roots, a baseline or export that is not a mapping, a
`plans` mapping, a throttle integer past the float range) and the `time.sleep` errors a throttle raises out of `run()` after its
host is scanned (`OverflowError` past the `time_t` range, `OSError` EINVAL when the CLOCK_MONOTONIC deadline passes 2^63 ns);
`request-coercion/tilde-nul-user-name` covers the `ValueError` (`embedded null byte`) `pwd.getpwnam` raises for a `~name` root
whose name holds a NUL, which `PythonOsPath.ExpandUser` raises as `PythonValueException`; `request-coercion/coerced-values`
compares the coercions that do not raise. A `~name` root is expanded from the password database on both sides (`getpwnam_r`
through `UnixPasswd` in the port, Linux only), trailing `/` stripped from the home; `close-r2/tilde-user-root` compares one.

Four dumps per case:

1. Port.
2. Fixed Python (`PARITY_MULTI_SERVER_FIXES=1`, `py_dump._MultiServerFixes`): `_normalise_config_id` is the port's rule (fix a,
   `@root{index}` suffix included, the index being the root's position in `plan.roots`) and fix b as below. It must equal the port
   dump byte for byte, `key_order` and cache digests included.
3. Fix b Python (`PARITY_MULTI_SERVER_FIXES=b`): stock ids. `_collect_secret_hits` refuses a root directory that cannot be listed
   or looked up with `DetectorIOError(root)` and runs `hunt_path` with `is_file()` and the file read tolerant of `OSError` (the
   file is skipped); a root whose `exists()` raises counts as existing; the detector's `_handle_error` skips every path but the
   root being scanned; a detected file whose whole text cannot be read, or is longer than the port reads whole, keeps its entry
   without its match; a file whose name is not UTF-8 is reported before it is sampled (the platform limit below). Each skip is
   recorded with its cause.
4. Stock Python, recording the path each failing host's scan raised on.

`multi_server_stock.py` checks the steps separately, so each difference is tied to its cause:

* stock against fix b, plan by plan (every record, skip, refused root and scan names its plan's position in the request, so plans
  sharing a host id never explain each other): equal results must come with no skipped file; a differing host is a fix b host only when
  stock failed on exactly a path the fix b run skipped or a root it refused, or when fix b reports `Permission denied: <root>`
  for a refused root that the stock run scanned without a single match (one divergence); an undecodable-name host only when
  both runs succeeded and every skip was an undecodable name (its evaluated count is one divergence). Each side's catalog
  aggregates must equal what its own drilldown rows give (`drift_count` over unique host ids, as `drift_stats` is keyed by host
  id; every other aggregate per plan row); the `self_test` `wrong-drift-count` of `runtime-r3/duplicate-host-drift-count-self-test`
  proves a drift count one below the right one, a drift count counted per plan row and a wrong severity are each rejected. Rows, availability and labels of a host id come
  from its last plan, so only a host id whose last plan fix b explains has its rows masked. An entry held by fix b hosts alone, present in one run only, or whose
  baseline record belongs to a fix b host is dropped from both sides (one divergence per side); in every other entry only the
  rows of fix b hosts that hold the entry in either run, the aggregates they feed and, when it shows a fix b host, the diff pane
  are left out (one divergence), so the other hosts' rows and diffs are still compared. A fix b host that holds no entry keeps its
  row without `status` and `presence_status`, and its final progress line is not compared; an undecodable-name host drops only
  the entries of those files. A case with `stock_aborts` (stock raised out of `run()`) proves the abort instead: the recorded
  exception is the declared type, raised on a root the fix b run refused while that root's host was being scanned, and every
  progress line stock emitted before it equals the fix b run's, except the final line of a host fix b explains (one divergence);
* fix b against fixed, over every plan both runs scanned (fix b hosts included, so nothing on them is hidden from this step): the
  skips must be identical; each fix b id whose files are exactly one fixed id's files is renamed (`config_id`, every
  `{label}:{id}` string and the unified diff header lines) and compared after re-sorting; every other id collided, and its
  entries and the fixed entries its files went to are divergences (one each). Result exemptions are per plan, never per host
  id: only a plan whose own scan gave two files one stock id may report fewer evaluated configurations in fix b (same suffix),
  and, when the cache already held the host's entries (`--runs 2`, `algorithm-r3/collision-chain-runs2`, or an earlier plan of
  the same host id), `used_cache` false in fix b where the fixed run has true: the collided stock ids shared one cache entry,
  which the last file written overwrote. Each is one divergence; no other field, and no other plan of that host id, is exempt.
  The `self_test` `wrong-used-cache` of `runtime-r3/duplicate-host-mixed-outcomes` proves a flipped `used_cache` is rejected on
  every plan outside that exemption, a failed plan and uncollided plans of a repeated host id included;
* the summary counts once an entry was dropped or a masked drift count changed, and the cache listing (a function of the ids; the fixed cache is compared with
  the port) are not compared. The case's `fixes` list must name exactly the fixes (`a`, `b`, `undecodable-name`) that show up.

Fix d on this surface: namespaced xml reaches `_canonicalise` (the cached canonical payload) and `build_unified_diff` (which
canonicalises both payloads again). With `PARITY_PORT_CLI` set, every Python run hands any xml text mentioning `xmlns` to the
port's `parity-dump canon` (over the exact text, written to a scratch file) and uses its canonical text, so the diff, digests,
stats and cache entry are CPython's own over the port's canonical texts. Each text whose port canonical differs from Python's is
counted as an expected divergence; the canonical texts themselves are compared on the `canon` surface, where the namespaced
`fixtures/multi-server/*/localization/Strings.resx` are covered. The case trees under `tools/parity/cases/multi-server/` hold no
namespaced xml.

A multi-server case must not hold a format the Python catalog rejects (fix c changes `catalog_*` metadata and so config ids);
`py_dump.py multi-server` exits 3 when one appears.

| Case | Python behaviour | Port behaviour | Basis and harness handling |
|---|---|---|---|
| A root whose lookup fails with an error `Path.exists()` does not ignore (`EACCES` inside a directory that cannot be searched, `ENAMETOOLONG`) | `exists()` raises out of `run()`: the whole scan fails | The root counts as existing and is refused: the host fails with `permission_denied` and `Permission denied: <root>`, the rest of the scan completes | Fix b: a root that cannot be read fails its host only. Python's own `DetectorIOError` reports every root I/O error as `permission_denied`, whatever the errno. Cases `root-lookup-refused` and `root-name-too-long` (`stock_aborts`); the fixed and fix b runs make `exists()` tolerant |
| A detected file whose read fails after it was sampled (an I/O error, or the file made unreadable between the sample and the read) | `_read_text` raises: the host goes offline | The file is skipped (fix b) and the host succeeds | Fix b. No case: it cannot be produced on demand without a race; the fixed and fix b runs model it (`py_dump._MultiServerFixes._readable`) |
| A detected, readable text file longer than `MultiServerRunner.DefaultMaxTextBytes` (`0x3FFFFFDF / 6` = 178,956,965 bytes) | `_read_text` reads it whole; the file is recorded and the host succeeds | The file is not read: it is skipped as unreadable (fix b) and the host succeeds with one configuration fewer. The limit bounds only the text read, which then fits one runtime string (at most 0x3FFFFFDF characters). The strings built from a text below it are not bounded: indented canonical JSON and the cache entry's JSON (`json.dumps` escapes a control character as six characters, and the entry adds the metadata and signature) can pass the longest runtime string for a text near the limit, and the runtime then raises `OutOfMemoryException`, which `RunPlan` does not catch, so `Run` throws where Python completes | Runtime limit handled as fix b. No case (a file that size is not committed or generated); the fixed and fix b runs apply the same limit, and `MultiServerPathAndCacheTests.AFileLongerThanTheReadLimitIsSkippedAndTheHostSucceeds` pins the skip. The `OutOfMemoryException` past the string length has no case or test (it needs a text of hundreds of megabytes) |
| A file whose Linux name is not valid UTF-8 | `os.scandir` keeps the name through `surrogateescape`; the file is scanned and recorded | The runtime cannot name the file (see "Platform limits"): the detector reports it and it is skipped; the host succeeds with one configuration fewer | Platform limit `undecodable-name`: case `runtime-r1/undecodable-name` |
| A cache entry holding JSON past `json.loads` limits (4300-digit integers, 9999 nested containers) | `ValueError` / `RecursionError` escapes `DiffCache.load`: the host goes offline | The entry is ignored and recomputed, as any other entry that is not valid JSON | Interpreter limit (see "CPython limits inside `json.loads`"); the runner writes every entry itself, so no case |
| Exception text of a host that fails offline | `Scan failed: ` + `str(exc)` | The same for every error the cache raises (`[Errno N] <strerror>: '<entry path>'`, `UnicodeDecodeError` and `AttributeError` text, `PythonOSError`, `PythonUtf8`); any other exception carries the runtime's message | Runtime-specific text for the defensive branch. No case: a plan file cannot make a fresh cache fail; `MultiServerPathAndCacheTests` and `MultiServerUnitTests` pin the cache messages against CPython's |
| Empty `plans` | `run([])` returns `catalog` and `drilldown` as empty mappings (the source comments that this contradicts every other response) | Empty arrays | The model's `Catalog` and `Drilldown` are arrays and cannot hold a mapping; the GUI facade answers an empty plan list itself before the runner is called. Cases `algorithm-r2/empty-plans`, `algorithm-r2/plans-absent` and `request-coercion/plans-string` declare `"divergences": ["empty-response-mappings"]`: `run_parity.sh` requires the fixed dump's response to be exactly `results []`, `catalog {}`, `drilldown {}` and the port's `[]`, `[]`, `[]`, respells the two mappings as arrays in the fixed dump and byte-compares the rest (one divergence) |
| Cache entry writes | `write_text` opens the entry with `open(path, "w")` and writes it in place | Written to a temporary file in the entry's directory and renamed over the entry, so cancellation or a crash never leaves a partial entry. Before that, an existing regular entry is opened for writing without truncation, so a read-only entry fails as `open` fails (offline); when the directory refuses the temporary file, the entry is written in place as Python writes it, so a writable entry in a directory that refuses new names is rewritten and a missing one fails with Python's error. The host outcome and message are therefore Python's. What still differs is the file itself: a replaced entry is a new inode with the default mode, so it drops extra hard links and the old entry's mode and owner, and an entry that is a FIFO is replaced where Python's `open` blocks | Runtime choice (plan phase 5: atomic cache writes). No case: `run_parity.sh` starts every case with a fresh cache. `MultiServerPathAndCacheTests` pins both permission outcomes against CPython's on the same steps, and `MultiServerPortFixesTests.CancellationLeavesNoPartialCacheFile` pins the atomic rename |
| Two plans with one host id | `emit_progress` drops a repeat of the host's last status and message inside 50 ms, so the progress list depends on wall-clock time | The same rule, per run | Compared: `algorithm-r1/duplicate-host-id` and `runtime-r1/duplicate-host-ids`, whose scans take longer than the window |
| Progress throttle state | Process-wide `_progress_events` | One `ProgressThrottle` per run | `py_dump.py` resets the state before every run, so `--runs 2` compares per-run suppression |

## Python raises on unpaired surrogates (not reproduced)

A Python `str` can hold an unpaired surrogate (`json.loads` of a `\ud800` escape, a plan value, a `surrogateescape` byte name);
any strict UTF-8 encode of it raises `UnicodeEncodeError: 'utf-8' codec can't encode character '\udXXX' in position N:
surrogates not allowed`. Where only that raise takes a host offline or aborts the run, the port keeps working: .NET encodes the
surrogate as U+FFFD for hashes and cache files (operator decision). A root holding one is never opened under a guessed
spelling, and no path through a symlink whose target is not UTF-8 is looked up under the U+FFFD spelling the runtime gives
that target. `MultiServerSurrogateTests` pins each port behaviour.

| Trigger | Python behaviour | Port behaviour | Case |
|---|---|---|---|
| A lone surrogate in detection metadata, such as a JSON top-level key `"\ud800key"` in `top_level_keys` | `DiffCache.save` opens the entry with `write_text` (truncating it), then the strict encode of `json.dumps(..., ensure_ascii=False)` raises: the host goes offline (`Scan failed: ...`) and leaves an empty entry file, which `DiffCache.load` later treats as missing | The entry is written with U+FFFD and reused on the next run; the host succeeds | `algorithm-r3/surrogate-metadata-cache` |
| A lone surrogate in a `host_id` whose scan builds at least one record | `sha256(f"{host_id}:{config_id}:...".encode())` raises: the host goes offline (a host without records succeeds) | The signature and entry name hash the id with U+FFFD; the host succeeds | `algorithm-r3/surrogate-host-id` |
| A lone surrogate in a plan label that heads a non-empty unified diff (the baseline plan's label, or the label of a host whose record differs from the entry's baseline record) | `build_unified_diff` clamps the diff with `len(diff_text.encode("utf-8"))`, which raises out of `run()`: no response. A label heading only empty diffs raises nothing (see the port-model limit below) | The catalog is built with the label kept in the diff headers | `algorithm-r3/surrogate-label`; `close-r2/surrogate-label-runs2` (run 1 raises, so Python never starts run 2); `close-r2/surrogate-host-id-and-label` (a host id holding a surrogate first takes that host offline, then the label aborts `run()`) |
| A root whose expansion encodes a name holding a lone surrogate: `~<name>` (`os.path.expanduser` hands the name to `pwd.getpwnam`, which encodes it with the filesystem encoding) or `${<name>}` (`os.path.expandvars` looks the name up in `os.environ`, whose key encode is the same). A surrogate outside a name (`$HOME\ud800`, `~/\ud800`) is never encoded here | `UnicodeEncodeError` out of `Plan.from_mapping` / `_build_plans`: the request aborts before `run()`, with no progress, result or cache entry. A `\udc80`-`\udcff` surrogate encodes to its byte under `surrogateescape` and is looked up (not found on any realistic system: the path stays unchanged) | `PythonOsPath.ExpandUser` returns a `~<name>` path unchanged, without a lookup, when the name holds an unpaired surrogate, and `ExpandVars` finds no variable of that name, so the root keeps its surrogate and decision R leaves it out: the other roots are scanned, and a plan with no other root fails `not_found` with `No accessible roots.`, listing its roots | `close-r2/surrogate-expanduser-root` (`~\ud800/x` beside a valid root), `close-r2/surrogate-expandvars-root` (`${\ud800}/x` beside a valid root, and a plan whose only root is `${HO\ud800ME}`); `MultiServerSurrogateTests.ARootWhoseExpansionEncodesAnUnpairedSurrogateIsLeftOutAndTheRestIsScanned`, `PythonOsPathTests.PosixExpandUserNeverLooksUpANameHoldingAnUnpairedSurrogate` |
| A root holding a lone surrogate (decision R) | `'\ud800'` cannot be encoded for `stat`, so `Path.exists()` returns False: not found. `'\udcff'` is the `surrogateescape` spelling of byte 0xFF: when an entry with that byte name exists, `exists()` is True, the root is scanned and `root.resolve()` in the root fingerprint raises, so the whole host goes offline; otherwise not found | Never looked up: UTF-8 replacement would name the U+FFFD entry, a different directory. The root is absent from `existing_roots`; a host whose only roots hold one fails `not_found` with `No accessible roots.` | `algorithm-r3/surrogate-roots` compares both spellings byte for byte next to a directory named U+FFFD (both sides not found). `close-r1/mixed-valid-and-udcff-root` puts a `'\udcff'` root over an existing byte-0xFF directory beside a valid root and a U+FFFD directory (Python offline, the port scans only the valid root). The `'\udcff'` root as the only root (Python offline, port not found) has no case, as the harness requires the port to scan an offline host; `MultiServerSurrogateTests.ARootHoldingAnUnpairedSurrogateIsLeftOutOfTheExistingRoots` and `AnOnlyRootHoldingAnUnpairedSurrogateIsNotFound` pin both |
| A root that is, or lies below, a symlink to a directory whose Linux name is not UTF-8 (the root string holds no surrogate) | `exists()` is True and the scan runs; `root.resolve()` in the root fingerprint returns the `surrogateescape` name (`'\udcff'`) and `sha1(...encode())` raises, so the host goes offline | The kernel follows the link, so the directory is scanned and the host succeeds; the fingerprint hashes the physical path with U+FFFD for each byte sequence that is not UTF-8 (link targets are read as bytes, `UnixPathWalk`) | `close-r1/link-to-undecodable-root` (a root on the link and one below it; trees from `close-r1/generate_trees.sh`); `MultiServerSurrogateTests.ARootThroughALinkToADirectoryWithoutAUtf8NameIsScanned` |

Harness handling: a case declaring `"divergences": ["python-surrogate-encode"]` may have Python dumps that end with that
`UnicodeEncodeError` (all three, or none; any other Python failure still fails the case) or fixed results with a host offline on
that message. `py_dump.py` records where an exception ended the dump (`stage` `build_plans` or `run`, and the run, from 1).
`multi_server_surrogates.py` then compares the fixed dump with the port's instead of the byte comparison:

* An offline host: the port must have scanned it (`succeeded`, `found`); its result, its plan's final progress line, every entry
  it holds in the port (dropped from both sides), its rows' `status` and `presence_status` in the other entries, the summary
  counts once an entry was dropped, and the cache entries named by its ids are not compared; everything else is, key order
  included.
* A `run()` that raised: the fixed records hold the progress, host results and cache entries left before the raise, which must
  equal the port's, with any host offline on a surrogate encode before the raise excluded as above; the port's catalog,
  drilldown and summary have no counterpart. When the raise ended an earlier run than the case's last (`--runs 2`), Python never
  started the later runs: `run_parity.sh` dumps the port again with `--runs` set to the run that raised, over a fresh cache, and
  compares that dump; the port's later runs have no counterpart (one more divergence).
* `_build_plans` that raised: there is no Python response. The exception must be the one the case's first root raising a surrogate
  encode gives when expanded; the port must answer every plan, a plan with a root left must have succeeded with exactly those
  roots (each of which must exist), and a plan with none must have failed `not_found` with `No accessible roots.`. The port's
  response is not compared with a Python response.

Each item is one expected divergence. `py_dump.py` lists an entry that is not a JSON object with signature null, as
`DiffCache.load` treats it.

### Port-model limit: an unpaired surrogate in `diff_summary`

This path raises nothing in Python. A plan label holding a lone surrogate that heads only empty diffs (every host's record equals
the entry's baseline record) reaches the drilldown's `diff_summary` (its
`from`, `to`, `from_label`, `to_label` and `versions` strings) with the surrogate kept, and Python returns the response.
`ConfigDrilldown.DiffSummary` is a `JsonElement`, which holds UTF-8: `JsonSerializer.SerializeToElement` writes U+FFFD for the
surrogate, and a `JsonElement` holding the JSON escape of one throws on every later write, so the model cannot carry it. Every
other string of the response keeps the surrogate. In the GUI, labels reach the runner from `ServerScanPlan` models: a label
restored from the session cache went through `System.Text.Json`, whose writer had already replaced any lone surrogate with
U+FFFD, so it holds none; a label typed into a host card is the text box's string as it stands, and nothing between it and the
runner replaces a lone surrogate, so only such a label could show this difference. `MultiServerSurrogateTests.ALoneSurrogateInALabelReachesTheDiffSummaryAsAReplacementCharacter`
pins the port. Harness handling: `close-r2/surrogate-label-empty-diffs` declares `"divergences": ["diff-summary-replacement"]`;
`run_parity.sh` replaces every escaped lone surrogate in each fixed `drilldown[].diff_summary` with U+FFFD and, where that equals
the port's `diff_summary` exactly (key order included), writes the port's value into a copy of the fixed dump, which then goes
through the byte comparison (one divergence per entry). No other field is touched, a declared case that shows no such entry
fails, and an undeclared one fails the byte comparison.

## GUI-only behaviour (no Python oracle)

| Behaviour | Port | Harness handling |
|---|---|---|
| Relative scan roots and the legacy diff cache (`DriftbusterBackend.RunServerScansAsync`) | The scan runs in process, so a relative root resolves against the process working directory, as every other facade call resolves paths. The Python facade started `python -m driftbuster.multi_server` with its working directory set to the checkout it found above the working directory, the application base or the process directory (by `pyproject.toml` or `src/driftbuster/multi_server.py`), so relative roots resolved there. The legacy `<checkout>/artifacts/cache/diffs` migration now finds the checkout by `DriftBuster.sln`, which sits beside `pyproject.toml` at the repository root; `scripts/release_build.py` never packaged `src/driftbuster`, so no shipped build had a checkout at its application base to migrate from. The facade's cache directory goes through `DiffCache.ResolveCacheDirectory` as an explicit directory, the `_resolve_cache_dir(cache_dir)` branch the bridge reached (user home expanded, created, symlinks and `..` followed as the kernel follows them), and the migration copies into the directory the kernel reaches; `MultiServerCacheDirectoryTests` covers a data root through a symlink and an `XDG_DATA_HOME` with `..` after one. The GUI cannot send a relative root (`ServerSelectionViewModel.ValidateRoot`: "Path must be absolute.") | Not compared (no Python oracle for the facade). `DriftbusterBackendTests.RunServerScansAsync_executes_multi_server_runner` addresses the fixtures from the repository root |
| Diff planner file decoding (`DriftbusterBackend.DiffAsync`) | Reads each file with `StreamReader(UTF-8, detectEncodingFromByteOrderMarks: true)`: a UTF-8, UTF-16 or UTF-32 byte order mark selects the decoder, otherwise UTF-8 with replacement. Python's nearest reader, `multi_server._read_text`, decodes bytes as UTF-8 with replacement, so a UTF-16 file would become U+FFFD and NUL-interleaved text there. The GUI planner has no Python counterpart, so it keeps the BOM-aware reader (`DiffAsync_decodes_files_by_their_byte_order_mark`) | Not compared. The `diff` and `canon` parity surfaces never use the planner's reader: both dumps read bytes as UTF-8 with replacement (`py_dump._read_replace`, `ParityDump.ReadReplace`), pinned by `Diff_reads_a_utf16_file_as_utf8_with_replacement` |

## Platform limits

| Case | Python behaviour | Port behaviour | Harness handling |
|---|---|---|---|
| A Linux file or directory name that is not valid UTF-8 (bytes such as `0xFF`) | `os.scandir` keeps the bytes through `surrogateescape` (`'\udcff'`), so the walk yields a file and every surface opens and scans it, and walks into a directory and scans its children | .NET decodes listed names as UTF-8 with U+FFFD for each invalid sequence; the decoded name names nothing on disk. The port never opens a guessed name: `PythonPath.IsUndecodableName` (U+FFFD in the name and a failing `lstat`) marks the entry, `Detector.ScanPath` reports it through `HandleError` (`DetectorIOException`, "File name is not valid UTF-8") and `HuntEngine.HuntPath` lists it in `UnreadableFiles`. A directory is marked the same way (the entry itself, listed as if it were a file) and its whole subtree is neither walked nor reported: its children cannot be named either | No committed case (a committed name must be valid UTF-8 for git on every platform); `PythonPathTests`, `DetectorSpecialEntriesTests` and `HuntWalkEntriesTests` create one on Linux, and the multi-server case `runtime-r1/undecodable-name` generates one (see "Multi-server surface"). DriftBuster ships for Windows first, where names are UTF-16 |
| A symlink whose target is not valid UTF-8, on a path the port resolves itself (a `..` after the link, the root fingerprint, a cache entry or directory reached through the link) | The kernel resolves the target bytes; `..` steps to the parent of the directory the link names | .NET decodes a link target as UTF-8 with U+FFFD, and its file calls remove `..` lexically, so `PythonPath.KernelPath` walks every part up to the last `..` over bytes (`UnixPathWalk`: `readlink` and `statx` on the exact bytes), expanding a link only when a `..` steps out of it. The result is Python's whenever the directory reached has a UTF-8 name (`close-r1/dotdot-after-link-to-undecodable`, and `-trap`, where the U+FFFD spelling leads elsewhere). When it has none (`dotdot/deeper/..` with `deeper -> <0xFF>/sub`), no runtime string names it: `KernelPath` answers a path under `/dev/null/unreachable`, so nothing is opened or created and a multi-server root there fails `permission_denied` (Python scans it, then goes offline on the fingerprint encode). A cache entry that is a link to such a target is written in place through the link, as `write_text` writes it, not atomically, and an explicit cache directory whose physical path has no UTF-8 name is used through the spelling given, which reaches it (Python's `resolve()` gives the `surrogateescape` name) | The byte-compared `close-r1` cases above; `PythonPathTests` (`KernelPathNeverFollowsTheReplacementSpellingOfALinkTargetThatIsNotUtf8`, `KernelPathOfADirectoryWithoutAUtf8NameNamesNothing`, `ResolvePhysicalPathReadsLinkTargetsAsBytes`) and `MultiServerSurrogateTests` (`ARootWithDotDotAfterALinkToANameThatIsNotUtf8ReadsTheDirectoryTheKernelReaches`, `ARootWhoseDotDotEndsInADirectoryWithoutAUtf8NameIsRefused`, `ACacheEntryLinkedToANameThatIsNotUtf8IsWrittenThroughTheLink`, `ACacheDirectoryThroughALinkToANameThatIsNotUtf8IsUsedThroughTheLink`); `PythonPathManagedFallbackTests` runs the managed resolution other platforms use |
| FIFOs, sockets and devices in a walked tree | `Path.is_file()` is a `stat` that follows links: False for all of them | `PythonPath.IsFile` asks `statx` for the file type on Linux (never opening the file) and uses the directory, device and reparse-point attributes on Windows. On other Unix systems (no `statx` binding) it falls back to the attribute checks, which cannot tell a FIFO from a regular file. The GUI diff planner refuses a non-regular file with `InvalidOperationException` before opening it | `run_parity.sh` detect, decode, hunt and secrets add a FIFO, a Unix socket and a link to `/dev/null` to their generated trees; every port dump runs under `timeout` (`PARITY_PORT_TIMEOUT`) |

## Hunt rule search time

`PythonPattern` returns `_sre`'s results but reuses work within one subject (see `ReMatcher`): the end of an unbounded
single-character run, a backward scan for a tail's first character, and the positions a position-only tail already failed
from. The shipped `feature-flag` and `service-endpoint` patterns (`key\s*=\s*['"][^'\"]*(...)[^'\"]*['"][^\n]*value\s*=...`)
retry `[^\n]*value` from every `key="..."` opening on a line, which is quadratic in CPython and was in the port; with the reuse
the port's work is linear in the line length. `PythonPatternTests.TheFeatureFlagRuleDoesLinearWorkOnAPathologicalLine` bounds
the matcher's work (opcodes plus characters scanned) at 64 units per character on 128 KiB lines of `key="flag" `,
`key="flag" v ` and `<add key="featureToggle" data="1"/>`, and the `POSITIONAL_TAILS` cases of `gen_regex_cases.py` compare the
reuse against CPython's `finditer` and `search`. A tail may hold groups and alternatives of such nodes (their marks lie above the
restored `lastmark` when the tail fails), so the second `feature-flag` pattern (`<feature\b[^>]*\b(enabled|value)\s*=...`), which
retries its tail from every position `[^>]*` backs off to for every `<feature` opening, is linear as well:
`TheFeatureElementRuleDoesLinearWorkOnAPathologicalLine` bounds it the same way on `<feature enabled=x ` and `<feature value `,
and `tools/parity/cases/hunt/hunt-secrets-r1-close/feature-element-enabled-no-quote-16k.config` is compared like any other file. `tools/parity/cases/hunt/hunt-secrets-r2/minified-single-line-96k.config` is the
slowest hunt parity case in CPython and is compared like any other file.

`detect` and `decode` can show only fix c, the interpreter-limit cases, the SQLite URI names, the Unicode 16.0 repr case and
the unreadable-root case. `diff` and `canon` add fix d (and `diff` the unpaired-surrogate interpreter error), `hunt` adds
fixes e and b, `secrets` adds fix g, and `multi-server` adds fixes a, b and d, the undecodable-name platform limit, the unpaired-surrogate raises and the `diff_summary` port-model limit. `detect` without `--plugins` runs the full default registry on both sides
(all eleven plugins in Python priority order). Any other difference is a port defect.
