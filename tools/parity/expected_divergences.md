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
| e. double-escaped install-path hunt regex | Regex never matches a Windows path | Regex matches install paths | hunt, capture | The Python dump runs with the corrected pattern injected (`PARITY_HUNT_FIX_E=1`) and every record is compared exactly; a stock Python dump must equal it on every record whose rule is not `install-path`, the differing `install-path` lines are counted as expected divergences, and for `tools/parity/cases/hunt` the port is asserted to find the intended Windows path where stock Python does not (details below). capture: see "Registry, SQL export, reporting and capture" |
| g. redaction loop that never returns | `copy_with_secret_filter` repeats forever on a line where rules keep matching inside the `[SECRET]` text they inserted | Past a fixed budget of such replacements the line goes back to its last replacement that consumed source text and the looping rules are stopped on it | secrets, run-profile | `py_dump.py` gives each file `PARITY_SECRETS_TIMEOUT` seconds; `secrets_guard.py` replaces a file Python never finished with its reference model's record (CPython `re`, the same rule), which must carry a guard, and the port is compared exactly (details below) |
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
| `NaN`, `Infinity` or `-Infinity`, or an integer literal of more than 4300 digits, as a value inside the 9998th nested container (the deepest `json.loads` decodes under `py_dump.py`) | The C scanner has no recursion budget left for the call it makes to decode the literal (`parse_constant`, and the integer conversion) and raises `RecursionError: maximum recursion depth exceeded while calling a Python object`; `1`, `"s"`, `1.5`, `true`, a 4300-digit integer, a 4301-digit float and an empty container still decode there. The depth moves with the frames the caller holds | `PythonJson` decodes the constant, and raises the digit limit's `ValueError` for the integer, at every depth up to the nesting limit: the schedule manifest or state loads (or fails with that `ValueError`) where Python raises. Reach: every `PythonJson.TryLoadsOrRaiseLimits` caller (the schedule manifest and state, `load_profile` / `list_profiles`, the detection profile commands' `_load_json`), only on a document nested to the limit with such a literal at the bottom | Interpreter limit, not reproduced. `schedule/closeout-scheduling-r2/json-nesting-9998-nan-innermost-manifest`, `json-nesting-9998-negative-infinity-innermost-state` and `json-nesting-9998-bare-array-int-limit-inside` declare `json-call-budget` in their `divergences` file: Python's dump runs with `PARITY_SCHEDULE_JSON_CALL_BUDGET=1` (`py_dump._JsonCallBudget`: only when `json.loads` raises exactly that `RecursionError`, the text is decoded again with each such literal in a value position replaced by a sentinel string, and succeeds only if that decode does; the sentinels then become the floats and the first integer past the limit raises `int()`'s `ValueError`), and on every step where the seam decoded, the same step run stock must end with exactly that `RecursionError`; the port is compared byte for byte with the seam's record (one divergence per such step). A declared case where the seam never decodes fails; `json-nesting-9998-int4300-innermost-manifest` is compared as is |

Reproducing an interpreter crash is not a behaviour worth porting; the port parses both inputs in the json
plugin. Inside registry-live the limits are reproduced, because there Python survives them and returns None. The schedule manifest and state
loaders, `load_profile` / `list_profiles` and the detection profile commands' `_load_json` raise the limits' `ValueError` / `RecursionError` as
Python does (`PythonJson.TryLoadsOrRaiseLimits`, see "Scheduling").

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
| A `..` part after a symlink (`link/../x`) | The kernel steps to the parent of wherever `link` leads | .NET removes `..` lexically before every file-system call, so every call the walks, the detector, the hunt, the secret copy, the SQLite count and the multi-server runner make goes through `PythonPath.KernelPath`, which resolves every part up to the last `..` physically and appends the rest as written; enumerated paths keep their `..` (`PythonPath.Absolute`, not `Path.GetFullPath`), directories are created by `PythonPath.MakeDirectories`, which steps out of a directory it has just created as `mkdir(parents=True)` does (`new/../sub` creates `new` and `sub`), and a path compared the way `os.path.abspath` compares it is still normalised lexically | Compared, no divergence: detect and decode `generated/root with .. after a symlink`, hunt and secrets `generated/... root with .. after a symlink`, multi-server `algorithm-r1/dotdot-through-symlink`, `runtime-r1/dotdot-through-symlink` and `runtime-r1/secrets-link-and-dotdot` |
| Walk order on Windows | `sorted(Path)` compares case-folded components on Windows | Components compared case-sensitively by code point on every platform (the posix order) | Not observable: the harness runs on Linux, where both sides agree |

Both walks are `sorted(root.glob(glob))`; the port runs a port of `Path.glob` (`Infrastructure/PythonGlob`), so a custom glob
follows a symlinked directory exactly where Python does (a wildcard part) and the default `**/*` never does.
Every other walk behaviour is compared directly: both dumps enumerate with their engine's own
`scan_path` (dangling symlink skipped, symlinked directory not followed, symlinked file scanned,
unreadable subdirectory skipped, unreadable file recorded as `{"path", "error": "DetectorIOError"}`,
missing or dangling root recorded with path `.`), scan with one detector so the
`--max-total-sample-bytes` budget stops the walk on the same file, and order records as the
engine yields them.

## Runtime Unicode tables (not divergences)

Python 3.13 carries Unicode 15.1 and .NET 10 carries Unicode 16.0. Every character property the port reads goes through
`Infrastructure/PythonUnicode`, which answers with the 15.1 tables: the general category (so `\w`, `\d`, `str.isalpha()`,
`str.isalnum()`, `str.isprintable()` and `repr`, `str.isidentifier()`'s categories, the case-ignorable and cased sets of `str.lower()`), the
decimal value (`int()`, `float()`, `str.format` field numbers and widths) and the digit value. It reads the runtime's tables less the code
points first assigned in 16.0, which are unassigned under 15.1 (the 16.0 digits U+1CCF0-U+1CCF9, U+11BF0-U+11BF9 and U+16D70-U+16D79 are
not digits to `int()`), and U+1171E, whose category 16.0 changed. `IsSpace`, line boundaries and the case mappings are identical over
every code point. `tools/parity/gen_regex_cases.py` writes the interpreter's category, decimal, digit, `isalpha`, `isalnum` and
`isprintable` tables; `PythonUnicodeOracleTests` and `PythonReOracleTests` compare them, every regex atom's code point set and the `_sre`
case tables with the port over every code point, exactly, and the category test prints the delta tables to paste when a runtime
changes its tables. Numeric values (`str.isnumeric()`) are not ported: the runtime lacks the Unihan numeric values of some ideographs,
and nothing the engine runs reads them. Compared: multi-server `runtime-r1/unicode-names` and `runtime-r1/baseline-astral-fallback`
(config id slugs), `tools/parity/cases/detect/binary-regl-r2/r2-regscan-repr-unicode16.json`, `profile-store/profiles-r3/*-unicode16-repr.json`,
`run-profile/profiles-r3/string-options-repr-unicode16` (`repr`), `profile-store/closeout-profiles-r2/diff/config-count-unicode16-digit`
(`int()`), `detect/closeout-unicode16/` (ini and conf tokens holding 16.0 letters) and `hunt/closeout-unicode16/` (`\b`, `\w` and `\d`
next to 16.0 letters and digits).

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
- `DetectorTests.ScanWithProfilesAttachesMatches` and `DetectorProfileReviewIgnoreTests.ScanWithProfilesReviewIgnore` run the
  real registry over a `DetectionProfileStore`; `DetectorIgnoreExceptionTests.ScanWithProfilesIgnoreException` and
  `DetectorTests.ScanWithProfilesFallsBackToFilename` drive `IProfileMatcher` with stubs, as the Python tests do.
- `ProfilesTests`: Python's `pytest.raises(TypeError)` for a mutator that is not callable, or returns something other than a
  profile, is a null mutator or a null result, the only such values the typed delegate admits.
- `DetectionProfileCommandsTests` mirrors the library half of `tests/cli/test_profile_cli.py`: each test runs its inputs through
  `DetectionProfileCommands` and makes Python's payload assertions. The exit codes, stdout, `--output`, `--indent` and
  `--sort-keys` of `test_profile_cli_summary`, `test_profile_cli_diff`, `test_profile_cli_hunt_bridge`,
  `test_profile_cli_summary_writes_output_file` and `test_handle_hunt_bridge_validates_payload`, and all of
  `test_parse_args_requires_command`, belong to the console tool.

- `RunProfilesTests` mirrors tests/core/test_run_profiles.py and `RealtimeTests` tests/secret_scanning/test_realtime.py, both over
  the ported `execute_profile`. `OfflineRunnerSourceTests` runs the four config-shape tests of tests/core/test_offline_runner.py
  (`test_load_config_accepts_string_and_object_sources`, `test_execute_offline_run_handles_optional_source`,
  `test_execute_offline_run_missing_required_source`, `test_offline_collection_source_validations`) against the run profile's
  structured sources: `load_config` is `RunProfile.FromDict` over the profile mapping, `execute_config_path` is `execute_profile`, and
  the manifest summary of a skipped optional source, which a run profile does not write, is asserted as the run's absence of files
  under the source's alias.
- `SchedulerTests` mirrors tests/run_profiles/test_scheduler.py. `ScheduleIntegrationTests` mirrors tests/scheduler/test_integration.py
  through `ScheduleCommands` (the `schedule due|mark-complete|skip-until|list` payloads and the state file) instead of `main(argv)` and
  stdout. `RunProfilesCliCommandsTests` mirrors the library half of tests/cli/test_run_profiles_cli_commands.py through
  `RunProfileCommands` (`test_parse_options_validates_format`, `test_list_profiles_reports_empty`, `test_create_list_show_and_run`,
  `test_run_command_loads_by_name`); `test_main_entrypoint_dispatches`, `test_main_returns_error_when_no_command` and
  `test_run_profiles_module_main` test `build_parser` / `main` dispatch and belong to the console tool. Python's `raise SystemExit(msg)`
  is `CommandExitException`; the console tool prints its message and exits 1, as it lets any other exception end the command.
- `OfflineRunnerSourceTests` asserts the skipped optional source's manifest summary on `ProfileRunResult.Sources` (`skipped`, reason
  `no-matches` / `missing`), and adds the collection and validation tests of the same Python file that structured profiles now follow
  (`test_execute_offline_run_respects_exclude_patterns`, `test_execute_offline_run_deduplicates_recursive_glob_matches`,
  `test_execute_config_optional_missing_file`, `test_execute_config_required_glob_without_matches`, `test_execute_config_skips_symlink`,
  `test_execute_config_excludes_single_file`), each with a mapping source added where Python's input has none so the profile is structured
  (`test_execute_offline_run_missing_required_source` too, which also asserts the message). `OfflineRunnerProfileValidations` calls
  `RunProfile.FromOfflineRunnerDict` (the reader `FromDict` hands a structured payload) with Python's inputs and messages; the run profile
  carries no tags, so Python's final `profile.tags == ("prod",)` is the check that `"tags": "prod"` is accepted.
  `OfflineCollectionSourceValidations` keeps Python's `destination_name` assertion (`/` gives `source_07`), which holds for every aliasless
  source under the recorded `source_NN` decision; the test also pins that decision for a named path and the alias case.
- `DetectorTests.ScanWithProfilesRequiresStore` expects `PythonValueException("profile_store must be provided")`, Python's `ValueError`.
- `LiveHivesTests.OfflineRunnerUsesExplicitRoots` (tests/registry/test_live_hives.py) and `SqlSnapshotsTests.OfflineRunnerSqlSnapshotSource` /
  `OfflineRunnerSqlSnapshotOptional` (tests/offline/test_sql_snapshots.py) run `offline_runner.execute_config` in Python and assert on the
  written manifest's `sources` entry (and `metadata.sql_exports`) and on the file under the staging data directory. The port has no
  offline runner (`driftbuster-offline-runner.ps1` collects files only, and a run profile refuses `registry_scan` and `sql_snapshot`
  sources); the mirrors run `RegistryScanCollector.Collect` and `SqlSnapshotCollector.Collect`, the ports of those two `execute_config`
  branches, and make the same assertions on the summary, metadata entry and file each returns.
- `OfflineSqlSnapshotSourceTests` mirrors the SQL tests of tests/offline/test_offline_runner_config_helpers.py.
  `OfflineRunnerProfileWithRegistryAndSqlSources` reads the profile with `RunProfile.FromOfflineRunnerDict`, which refuses the
  `registry_scan` entry (the recorded run profile decision) where Python's `OfflineRunnerProfile.from_dict` builds three sources, so the
  mirror asserts that refusal and parses the `registry_scan` and `sql_snapshot` entries with their own source types.
- `HtmlReportTests.RenderHtmlReportToleratesMappingInputsAndCorruptEntries` gives the corrupt match `double.NaN` where Python gives
  `confidence="invalid"` (the typed match holds a double, so the `Confidence: invalid` cell Python renders has no port input; the
  string reaches the summary through `RenderDetectionSummaryToleratesInvalidConfidence`), and `RenderHtmlReportIncludesSections`
  builds `DiffStats(1, 0, 0)` where Python's `DiffResult` carries `stats={"added_lines": 1}`, so the port renders three stat rows where
  Python renders one; the assertions are Python's.
- `JsonAdapterTests.LegacyModuleReexportsNewHelpers` asserts that each `JsonReport` forwarder returns what the `JsonLinesReport` helper
  it re-exports returns for the same inputs, where Python's `test_legacy_module_reexports_new_helpers` asserts object identity
  (`legacy_json.iter_json_records is iter_json_records`): the typed port re-exports as static methods that call through, so no two
  method objects are the same and identity has no port equivalent.

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
| Group names | `str.isidentifier()` (XID_Start / XID_Continue with NFKC closure) | The Unicode 15.1 general categories behind ID_Start / ID_Continue (`PythonUnicode`) | Not ported: a name spelled with one of the few NFKC-unstable code points compiles in the port only |
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

## Detection profiles (`Profiles/Detection`)

`run_parity.sh profile-store` compares every payload under `tools/parity/cases/profile-store/` at any depth outside `diff/` directories
(`summary`, `applicable_profiles` and `matching_configs` for the case's tags and path, `find_config` for every identifier, `to_dict` and its round
trip, or the exception type and message; extra dump arguments one per line in `<name>.args`, a line `json:"..."` being that JSON string decoded)
and every `diff/<case>/` pair through `diff_summary_snapshots`, byte for byte with key order. The one normaliser,
`tools/parity/profile_text_fields.py`, covers the first two rows below. It replaces a Python value of `application`, `version`, `branch`,
`expected_format`, `expected_variant` or `description` that is not a str or null by the port's value when that value is exactly its `str()` (a
mapping's keys taken in the order the record's `key_order` gives), and sets that field's `key_order` child to null, a str's. It does so only at
the record positions holding those fields (a profile's `description` in `summary`, `to_dict` and `round_trip`, a config's text fields in
`to_dict` and `round_trip`, `expected_format` / `expected_variant` of each match in `matching_configs` and `find_config`); a key of the same name
inside user `metadata`, or anywhere else, is compared as is, and `run_parity.sh profile-store` first runs the script's `--self-test`, which fails
the surface unless a stringified metadata value under such a name still differs after normalisation
(`profiles-r1/empty-application`, `profiles-r1/fallback-mixed-types`, `profiles-r2/description-nested-mapping`,
`profiles-r2/text-fields-falsy-values`). It replaces a whole Python record by the port's only when the port record is exactly the store's
duplicate `ValueError` (`Duplicate config identifier 'T' within profile 'P'`, `Config identifier 'T' already registered under profile 'Q'` or
`Profile 'T' is already registered`) and Python's `to_dict` holds the two distinct values with that one `str()` text, at least one a str, where the
message places them (`profiles-r2/int-and-str-id`). Each record changed is one divergence. Records are split on `\n` only, so a raw U+0085,
U+2028 or U+2029 inside a record is compared like any other character (`profiles-r2/path-separators-and-dotdot`). The other rows are outside the cases;
`DetectionProfileStoreEdgeTests` and `DetectionProfileCommandsTests` hold CPython 3.13's output for each of them and pin the port's side.

| Case | Python behaviour | Port behaviour |
|---|---|---|
| A JSON number, bool or null as `id`, `name`, `description`, `application`, `version`, `branch`, `expected_format` or `expected_variant` (also a list or dict in the fields other than `id` and `name`), in `from_dict` and `_store_from_payload` | Stored as the JSON value: `to_dict`, `summary` and the hunt bridge write it back as that value, and `1`, `1.0` and `true` are one identifier | The typed profile stores `str(value)`, the text Python formats into the `application:` / `version:` / `branch:` tags, so matching agrees; the outputs carry the text (`"2"`, `"True"`), and `1`, `1.0` and `true` are three identifiers. A list or dict `id` or `name` raises `TypeError: unhashable type` where registration hashes it, as in Python |
| Two identifiers (or two profile names) that are distinct values with one `str()` text: `1` and `"1"`, `true` and `"True"`, `null` and `"None"` | Both registered: `config_ids` `[1, "1"]` | The store holds both as `"1"` and raises its duplicate `ValueError` (`Duplicate config identifier '1' within profile 'p'`, the cross-profile `already registered` form, or `Profile '1' is already registered`); the lenient reader raises the same, so the payload fails |
| `metadata` given as key/value pairs whose key is not a str (`[[1, 2]]`) | `dict([[1, 2]])` is `{1: 2}` | `PythonTypeException` "metadata keys must be str, not 'int'": metadata keys are strings |
| `_load_json` on text that is not valid JSON | `ValueError: Failed to parse JSON from {path}: Expecting value: line 1 column 7 (char 6)` (the `JSONDecodeError` text) | `Failed to parse JSON from {path}: invalid JSON document`: `PythonJson` reports a failed decode without its reason or position |
| `_load_json` decoding | `Path.read_text()` uses the locale encoding (UTF-8 on Linux and macOS, the ANSI code page on Windows outside UTF-8 mode) | Strict UTF-8 on every platform, with CPython's `UnicodeDecodeError` text |
| The `TypeError` from sorting names or config ids of mixed types in `diff_summary_snapshots` | The operand types named in the message follow the set's iteration order, which depends on randomised str hashing | The same exception type; the operands follow the port's insertion order |

## Run profiles (`Profiles/Run`)

`RunProfileStore` and `RunProfileExecutor` port `core/run_profiles.py`; `RunProfileStoreEdgeTests` holds CPython 3.13's `profile.json`,
`metadata.json` and `ProfileRunResult.to_dict()` output (the port writes them with `Canonicaliser.DumpsSorted(..., ensureAscii: true)`,
`json.dumps(indent=2, sort_keys=True)`'s ASCII escapes, platform line breaks as `write_text` writes them). `_collect_matches` globs with
`Infrastructure/PythonModuleGlob`, a port of the `glob` module (`glob.glob(recursive=True)`: hidden names, `**` following symlinked
directories, listing order), not `Path.glob`; `PythonModuleGlobTests` holds CPython's results.

Plan decision, structured sources: a source is `{path, alias, optional, exclude}`. A source with only a path (a string, or a mapping whose
alias is blank or falsy, `optional` falsy and `exclude` empty) is written as a bare string. A profile whose sources are all path-only runs
exactly as `execute_profile`. A profile with a structured source (`RunProfile.IsStructured`; in `profile.json`, `RunProfile.IsStructuredPayload`:
a mapping that sets alias, optional or exclude, names `registry_scan` or `sql_snapshot`, or that `OfflineCollectionSource.from_dict` refuses)
follows `offline_runner` for the whole profile, string sources included:

| Step | Port behaviour (as `offline_runner.py`, except where noted) | `driftbuster-offline-runner.ps1` |
|---|---|---|
| reading `profile.json` | `OfflineRunnerProfile.from_dict`: non-blank `name`, non-empty `sources`, a mapping through `OfflineCollectionSource.from_dict` (non-empty `path`, blank or falsy `alias` dropped, `bool(optional)`, `exclude` a str or each item's `str()`), any other entry as the path `str(entry)`, a non-null `baseline` that is one of the paths, `tags` iterated, `options` a mapping, a truthy `secret_scanner` a mapping, `description` `str()`; the values are then held as `run_profiles` holds them | a string entry is a path; `alias` truthy, `optional` cast to bool, `exclude` a string or array |
| `alias` | the source's directory under the run is `_safe_name(alias)` | `Get-DbSafeName(alias)` (ASCII letters and digits only) |
| no `alias` | `source_NN`, as `execute_profile` names it (Python: the expanded path's name, else `source_NN`) | the resolved path's name, else its parent's, else `source_NN` |
| expansion and matching | `os.path.expanduser(os.path.expandvars(path))`, not made absolute; when the path as written holds no `*?[]`, the expanded path if it exists; otherwise the distinct `glob.glob(expanded, recursive=True)` results; nothing found raises `FileNotFoundError("Path does not exist: {path}")`, unless the source is optional (skipped, `ProfileRunResult.Sources` reason `missing` or `no-matches`) | `Resolve-Path` / `Get-ChildItem` |
| options | `build_context` gets the options as the payload holds them (`RunProfile.SecretOptions`), so a list's items are ignore values and a number or bool is none; `profile.json` holds their `str()` text, as `run_profiles.RunProfile.to_dict()` writes it | `Get-DbOptionList` over the options as the collector config holds them |
| source order | the sources are collected in declared order; the baseline is not moved to the front | same |
| collection | matches in posix order; a match that is a symlink is skipped; a match inside a directory already collected for the source is skipped; a directory's `rglob("*")` files copied in posix order under their path relative to it, a file under its name. Plan decision: nothing inside the run's own output is collected, whether a match names it or lies above it (physical paths compared): the profile directory (`<profiles root>/<profile directory>`, holding `profile.json` and `raw/`) and each directory above it the run created (the Profiles root, or the base directory, when it did not exist before the run). The offline runner writes its output elsewhere, so it never meets that tree: a match that is the run's own output is not a match, and a source whose every match is (`Prof*` when the run created `Profiles/`, `Profiles/ow*/raw` for the profile `own`) raises the `FileNotFoundError` of a source that matches nothing, or is skipped when optional (`closeout-profiles-r2/structured-required-glob-matches-only-run-created-profiles-root`, `structured-required-glob-matches-only-own-profile-dir`; `RunProfileCollectionLimitsTests`) | same |
| validation before the run (`save_profile`) | a required source whose path holds no glob character must exist (the same `FileNotFoundError` text); globs are checked when the run collects them; the baseline need not exist | none |
| `exclude` | a pattern matching the file's relative path (posix form) or its name with `fnmatch.fnmatch` skips it | `WildcardPattern` with `IgnoreCase` on the same two strings, empty patterns skipped |

The PowerShell column is the runner the offline collector package ships: `OfflineCollectorWriter` writes each source as `{path, alias (when set),
optional, exclude}`, which that runner reads as listed; `fnmatch` on posix is case-sensitive where `WildcardPattern` is not (the two agree on
Windows, where `fnmatch.fnmatch` lower-cases both sides) and `WildcardPattern` has no `[!...]` negation.

| Case | Python behaviour | Port behaviour |
|---|---|---|
| `json.loads` failure in `load_profile` / `list_profiles` | `JSONDecodeError` with its position text | `PythonValueException("Invalid JSON document: <path>")` (`PythonJson` reports no reason). The decoder's interpreter limits raise as in Python (`PythonJson.TryLoadsOrRaiseLimits`; see "Scheduling") |
| A directory source that contains the run's own output directory (a profile without structured sources) | `rglob` lists directories lazily while `_copy_file` writes into them, so the run copies its own copies again, one level deeper each pass, until a path is too long | `PythonGlob.Glob` lists the tree before the first copy, so only the files present when the source is walked are copied (a structured profile never collects that tree, see "collection" above; `run-profile/profiles-r3/structured-glob-star-matches-own-profiles`, `RunProfileStoreEdgeTests.AStructuredGlobNeverCollectsTheRunsOwnProfileDirectory`) |
| A `registry_scan` or `sql_snapshot` source in a run profile | `offline_runner` scans the registry (skipped off Windows) or writes a SQL snapshot | `PythonValueException("Run profiles do not support '<key>' sources.")`: neither is a run profile source (plan: registry scan and SQL export are their own commands). No harness case (the harness compares error types) |
| A source path holding an unpaired surrogate (phase 5 decision R), string or structured, literal or a glob's literal part, in validation and collection | `'\udcff'` is looked up as the `surrogateescape` byte 0xFF (not found unless such a name exists); a glob listing a directory spelled with any other lone surrogate raises `UnicodeEncodeError` from `os.scandir` | Never looked up under the U+FFFD spelling the runtime would give it: `RunProfileStore.Exists` / `IsDirectory` are false and `PythonModuleGlob` finds no literal part and lists no such directory, so a required source raises `FileNotFoundError("Path does not exist: ...")` and an optional structured source is skipped. A wildcard over listed names that are valid UTF-8 is unaffected. A byte-0xFF entry that exists is not found (Python finds it), and the `UnicodeEncodeError` is not reproduced. Compared: `run-profile/profiles-r3/string-source-lone-surrogate-beside-replacement-name`, `structured-source-lone-surrogate-beside-replacement-name`, `structured-glob-literal-part-lone-surrogate` (all beside a U+FFFD entry, where Python finds nothing); `RunProfileStoreEdgeTests.ASourcePathHoldingALoneSurrogateIsNeverLookedUpUnderItsReplacementSpelling`, `PythonModuleGlobTests.ALiteralPartHoldingALoneSurrogateMatchesNothing` |
| A source path whose expansion looks up a variable or user name holding an unpaired surrogate (decision R): `${\ud800}/x`, `$\ud800x/x` (`os.path.expandvars` looks the name up in `os.environ`, whose key encode fails) or `~\ud800/x` (`os.path.expanduser` hands the name to `pwd.getpwnam`, which encodes it), string or structured, optional included, in validation and collection. A surrogate outside a name (`$HOME\ud800`, `~/\ud800`) is never encoded here, and a `\udc80`-`\udcff` name encodes under `surrogateescape` and is looked up (not found) on both sides | `UnicodeEncodeError` escapes `_expand_path`: the whole run aborts, whichever source holds the path | `PythonOsPath.ExpandVars` finds no variable of that name and `ExpandUser` returns the `~name` path unchanged without a lookup, as for multi-server roots (see "Python raises on unpaired surrogates"), so the path keeps its text and the row above applies: a required source raises `FileNotFoundError("Path does not exist: ...")` and an optional structured source is skipped (reason `missing`) while the rest is collected. Compared against Python with these lookups made not found: `run-profile/closeout-profiles-r1/string-expandvars-surrogate-variable-name`, `structured-expandvars-surrogate-variable-name`, `structured-tilde-surrogate-user-name` (harness key below); `string-expandvars-udcff-variable-name` is compared as is. `RunProfileStoreEdgeTests.AVariableOrUserNameHoldingALoneSurrogateIsNotFoundAndTheSourceIsMissing` |
| Which of two missing required sources a structured profile reports | The first in declared order that the collection reaches (a glob that matches nothing included) | Validation reports the first missing literal path before the run collects any glob, so a glob declared earlier that matches nothing is reported only when no literal source is missing. Same exception type and message format. No harness case |

`run_parity.sh run-profile` runs every directory holding `profile.json` under `tools/parity/cases/run-profile/` at any depth (with `workdir/`):
both dumps copy `workdir/` into one scratch path (symlinks kept as links, as `shutil.copytree(symlinks=True)`), make it the working directory
and run with a fixed timestamp. For a profile without structured sources (a mapping holding only a path is run by `py_dump.py` as that path)
the dumps print `to_dict()`, the collected file set, the exception type and message, and every file under `Profiles/` (size, SHA-256, and the
text of `profile.json` and `metadata.json`), compared byte for byte with key order. Fix g applies here as on the secrets surface: `py_dump.py`
gives each `copy_with_secret_filter` call `PARITY_SECRETS_TIMEOUT` seconds and redoes a copy that has not returned with `secrets_guard.py`'s
reference model (which must fire a guard); the run's `redaction_guard` list is compared exactly (`fix-g-looping-rule`, one divergence). A case
directory may hold a `divergences` file (one name per line); the only name is `python-surrogate-name-lookup`, the expansion row above. For such a
case `run_parity.sh` first requires the stock Python dump to be exactly `{"error": {"type": "UnicodeEncodeError", ...}}` with the "surrogates not
allowed" message, then compares the port byte for byte with a Python dump run under `PARITY_RUN_PROFILE_SURROGATE_NAMES=1`
(`py_dump._SurrogateNameLookups`: an `os.environ` key or `pwd.getpwnam` name that cannot be encoded is not found, everything else is Python's own),
which must report at least one such lookup on its stderr (one divergence). A case that declares nothing is compared as is.

Structured sources (plan decision): compared against the offline runner, not `execute_profile`. For a structured profile `py_dump.py` builds
`OfflineRunnerConfig.from_dict({"profile": ...})` (no package, manifest or log) and runs `execute_config`; the harness compares the collected
file set (source, the alias directory when the source has an alias, the path under the source's directory, size and SHA-256, sorted), the
exception type and message, the secret findings and fix g redaction guards (each display path under its source directory, sorted), and
`profile.json`, whose expected text `py_dump.py` builds from `run_profiles.RunProfile.to_dict()` of the parsed profile with each source written
as the port writes it; after an error only the error is compared. The run layout, `metadata.json`'s other keys, the source order and the offline
runner's own logs, manifest and display paths are not compared. Each structured case is one divergence (`alias`, `optional-missing-source`,
`exclude-patterns`, `structured-secrets`, the `adv-structured-*` cases and the structured cases under `profiles-r1/` and `fixes-r1/`). The cases
avoid the differences listed in the tables above (a source without an alias named after its path, the two rows without a harness case).

## Scheduling (`Scheduling/`)

`ScheduleParsing`, `ScheduleWindow`, `ScheduleSpec`, `ProfileScheduler`, `ScheduleStore` and `ScheduleCommands` port `scheduler.py` and the
schedule half of `run_profiles_cli.py` over `Infrastructure/PythonDateTime` (`datetime` arithmetic, `replace`, `astimezone`, comparisons,
`isoformat` and the C `fromisoformat` parser), `PythonTimeDelta` (`delta_new`'s float accumulation and rounding, range errors, `repr`,
`total_seconds`), `PythonTime` and `PythonZoneInfo`. `run_parity.sh schedule` runs every directory holding `steps` or `config.json` under
`tools/parity/cases/schedule/` at any depth: each line of its `steps` is one `schedule list|due|mark-complete|skip-until` command run on both sides
against that side's state file from the previous step (seeded from the case's `state.json`), with `ProfileScheduler._now` fixed, and the payload
(or the exception type and message) and the state file text after the command are compared byte for byte with key order, except for the
two handled rows: the `JSONDecodeError` text below and the call budget at the nesting limit (see "CPython limits inside `json.loads`"). `gui/DriftBuster.Backend.Tests/Scheduling/Data/schedule_cases.json` holds CPython 3.13's results on this host's tzdata (regenerate
with `tools/parity/gen_schedule_cases.py`): adversarial `fromisoformat` strings, `parse_interval`, `_parse_time` and `_build_timezone` inputs,
`fromutc` / `utcoffset` around every offset change of eleven zones in eight years plus the post-2037 footer-rule years and seconds-offset years
of seven more, window `align` / `contains` around the 2025 and 2040 transitions, `next_after` chains, `from_dict` payloads and scheduler due /
complete / skip sequences, all compared exactly. `TzifZoneTests` compares footer rules (Julian and zero-based days, negative DST, hours
outside 0..24, offsets with seconds, malformed strings) with the C `ZoneInfo.from_file` over the same crafted bytes.

`PythonZoneInfo` on Unix reads the key's TZif file as CPython's C `_zoneinfo` does (`TzifZone`, `PosixTzRule`): the explicit transitions, the
wall-time transition lists per fold, the first standard-time type before them, and the POSIX TZ footer rule (or the last type) after them,
with offsets in whole seconds and `find_ttinfo` / `zoneinfo_fromutc`'s gap and fold rules, including the fold adjustment right after the last
explicit transition. It never consults `TimeZoneInfo`, whose footer handling misreads transition hours outside 0..24 and which rounds
offsets with seconds; over all 599 keys of `zoneinfo.available_timezones()` from 1900 to 2100 (every transition and footer transition
probed at ten-minute steps within two hours, and quarterly), `astimezone`, `fold` and `utcoffset` with fold 0 and 1 match CPython.

| Case | Python behaviour | Port behaviour |
|---|---|---|
| Any zone on Windows | `ZoneInfo` needs the `tzdata` package there (no system TZif files) | Windows has no TZif files either, so the offsets come from `TimeZoneInfo` under the IANA id (ICU converts it) and zoneinfo's fold and gap rules are rebuilt from them by hourly probing and bisection: two offset changes less than an hour apart are seen as one, offsets with seconds are rounded to whole minutes, and after 2037 the rules are the runtime's. Not observable on the Linux harness |
| Zone key lookup | `TZPATH` (`/usr/share/zoneinfo`, `/usr/lib/zoneinfo`, `/usr/share/lib/zoneinfo`, `/etc/zoneinfo`, `PYTHONTZPATH`), then the `tzdata` package; any file with a `TZif` header | On Unix the key must name a regular file with a `TZif` header under `TZDIR` or `/usr/share/zoneinfo`; on Windows, any IANA id `TimeZoneInfo` resolves. The runtime's case-insensitive `utc` and Windows ids (`Central Standard Time`) are refused, as zoneinfo refuses them |
| A TZif file whose transition index names no type | `_zoneinfo.c` checks `index > count` and reads past its array for `index == count` | `ValueError` for `index >= count`; the system zone files hold no such index |
| A directory that `mkdir(parents=True, exist_ok=True)` cannot create (the scheduler state's parent, a run profile's run directories, the multi-server cache directory) | `OSError` subclass and text naming the directory whose `os.mkdir` failed: `[Errno 17] File exists: 'afile'`, `[Errno 20] Not a directory: 'afile/..'` | On Linux the same (`PythonPath.MakeDirectories` runs `Path.mkdir`'s algorithm over `mkdir(2)`, `UnixMkdir`, raising `PythonOSError` with the errno), and the state file's `write_text` failure is the same `OSError` text (`IsADirectoryError` for a directory); `ScheduleStoreTests.WriteScheduleStateRaisesPythonsOSErrorForAStatePathItCannotWrite` holds CPython's. Elsewhere, and for a path holding a NUL or an unpaired surrogate, the directories are created through the runtime, whose exception type and text differ. No harness case: the dumps always use a scratch state file |
| `ScheduleWindow.contains` / `align` of a naive datetime | `astimezone` uses the system local zone | `NotSupportedException`: the scheduler only hands aware datetimes to the window |
| `ScheduleWindow(start, end)` with bounds that are not `datetime.time` | `ScheduleError("Window bounds must be datetime.time instances")` | The typed constructor admits only `PythonTime` |
| `Failed to parse schedules from {path}: ...` / `Failed to parse scheduler state from {path}: ...` | The `JSONDecodeError` text with its position (`Extra data: line 1 column 19 (char 18)`, `Unexpected UTF-8 BOM (decode using utf-8-sig): line 1 column 1 (char 0)`, ...), including text that holds a literal past the digit or nesting limit after the point where the decoder fails | `invalid JSON document` (`PythonJson` reports no reason), after the same prefix. Harness handling (`SCHEDULE_DECODE_TEXT` in `run_parity.sh`): a step whose records differ is accepted when both are `SystemExit` errors without output, Python's message is the prefix and one of the decoder's reason texts with its line, column and char, the port's is the same prefix and `invalid JSON document` (only the state file's scratch directory may differ, and only in the state message), and the state and key order are equal; one divergence per step. `schedule_decode_text_self_test` runs first and fails the surface unless the rule accepts a manifest and a state pair and refuses eight pairs that differ elsewhere (the port's text, a Python text that is not a decode error, the manifest path, a scratch-like manifest path, the state path outside the scratch directory, the state text, the error type, a port record with output). Cases: `schedule/closeout-scheduling-r2/json-int-4301-after-extra-data`, `json-nesting-limit-after-top-level-value`, `json-int-4301-after-unterminated-string-in-state`, `json-int-limit-zero-leading-then-digits`, `json-nesting-9998-state-bom-first` |
| `json.loads` interpreter limits in `schedules.json` or `scheduler-state.json` (the loaders catch only `JSONDecodeError`) | `ValueError: Exceeds the limit (4300 digits) for integer string conversion: value has N digits; ...` for a longer integer literal, `RecursionError: maximum recursion depth exceeded while decoding a JSON array from a unicode string` (or `JSON object`) for nesting past the C recursion limit, whichever the scanner meets first | The same types and messages at the first failure in text order (`PythonJson.TryLoadsOrRaiseLimits`), with the nesting boundary `json.loads` has under `py_dump.py` and a top-level script (9998 containers decode). Residual: the boundary moves with the interpreter frames the caller holds; through `python -m driftbuster.run_profiles_cli` 9994 containers decode and 9995 raise, so a manifest nested 9995 to 9998 deep loads in the port and not in that command. Compared at the `py_dump.py` boundary: `schedule/scheduling-r3/json-int-4301-digits-manifest`, `json-int-4301-digits-state`, `json-nesting-9997-manifest`, `json-nesting-9998-manifest`; `ScheduleStoreTests.TheLoadersLetTheDecoderLimitsEscape` |

## Registry, SQL export, reporting and capture (`Registry/`, `Sql/`, `Reporting/`, `Remote/`)

`run_parity.sh sql-export|report|registry-scan|capture` compares one record per case byte for byte (sorted keys, `key_order` included;
the records are read with Python's `json`, since jq reads `NaN` as null). The Python side is the engine itself behind `py_dump.py` seams:
`sql-export` builds the case's database from its SQL script (its bytes decoded as strict UTF-8 with the line endings untouched, on both
sides) with each side's own SQLite library and runs `write_sqlite_snapshot` with
`datetime.now` fixed; `report` renders the HTML report, JSON lines in both key orders, `summarise_detections` and
`build_snapshot_manifest` with the clock fixed, every stage rebuilding its inputs; `registry-scan` injects a fake `_Backend` (keys, values
with their `winreg` data, access refused, `OSError` / `RuntimeError` / `ValueError` raised) under the instrumented registry operations and
`registry_cli.main`, and prints the schema readers' results; `capture` runs `scripts/capture.py` `run`, `export-sql` and `compare` steps
in a copy of the case's `workdir/` with `datetime.now`, `time.monotonic`, `socket.gethostname` and `os.getenv` pinned and lists every file
the steps wrote.

The oracle runs CPython 3.13 against the system SQLite (`python -c 'import sqlite3; print(sqlite3.sqlite_version)'`) while the port bundles
SQLitePCLRaw's `e_sqlite3` (see the `SQLitePCLRaw.bundle_e_sqlite3` reference in `gui/DriftBuster.Backend/DriftBuster.Backend.csproj`, and
`SELECT sqlite_version()` through it), so the two libraries differ in version. Every value and error text the `sql-export` surface compares
matched between them on every case: the texts SQLite itself emits are the `sqlite_master.sql` schema text of each table (stored as written),
the `PRAGMA table_info` column names, the row values (integers, reals as `repr(float)`, text, BLOBs, NULL) and `sqlite3_errmsg` /
`sqlite3_errstr` texts of a refused open (`unable to open database file`), a refused statement or a failed step (`file is not a
database`, `attempt to write a readonly database`, `no such table`, ...), each raised as the `sqlite3` module's class for the result code
(`Sqlite3Cursor.ErrorClass`). The texts the `sqlite3` module itself produces (`Could not decode to UTF-8 column '...' with text '...'`,
`You can only execute one statement at a time.`, `query string is too large`, `the query contains a null character`) are reproduced from
CPython and do not depend on the library. A message or planner change between the two libraries would surface as a parity failure, not be
normalised away.

Windows executables: `gui/DriftBuster.Gui/app.manifest` and `cli/DriftBuster.Cli/app.manifest` (`ApplicationManifest` in each csproj)
declare `longPathAware` and `asInvoker`, as `python.exe`'s manifest does, so a database path longer than `MAX_PATH` without a `\\?\`
prefix reaches native SQLite's `CreateFileW` (`SqliteSnapshots.Open`) and the binary-hybrid plugin's `table_count` read as it does under
CPython when the `LongPathsEnabled` policy is on (plan decision 12; managed I/O prefixes `\\?\` itself either way).
`ApplicationManifestTests` pins the declarations; the behaviour is unverified on Windows until the phase 11 lab campaign.

Windows `OSError` texts: `open()` (`Path.read_text`, `Path.write_text`, `_load_json`, the capture, snapshot, report and scheduler state
writes, the diff cache) raises the CRT's errno-based `OSError`, `[Errno N] <_sys_errlist[N]>: '<path>'` (`Python/errors.c` reads the CRT
table for `0 < N < _sys_nerr`, else `FormatMessage` of `N` with trailing dots and white space removed), so the text is `[Errno 2] No such
file or directory`, `[Errno 13] Permission denied` there as on Linux, never the Win32 message for the number (`The system cannot find the
file specified.`). `PythonOSError.StrError` spells it so (`PythonOSError.WindowsStrError` holds the CRT table, read from CPython 3.14 on
the Windows lab guest; `PythonOSErrorTests`). The `os.*` calls (`os.mkdir`, `os.stat`, `os.scandir`) raise `[WinError N] <message>`
instead; the port creates directories through the runtime on Windows (the "Scheduling" row for `mkdir(parents=True)`), whose text
differs and is not compared there. The errno numbers are the MSVC ones CPython is built with (`winerror_to_errno`: `ENOTEMPTY` 41,
`EILSEQ` 42, `ETIMEDOUT` 138) and the `OSError` subclass follows the Windows `errnomap` (`PythonOSError.TypeName`: `ETIMEDOUT` 138 and
the Winsock codes CPython substitutes for the connection errnos, read from the same interpreter); none of those numbers comes back from
the file opens the port wraps.

| Case | Python behaviour | Port behaviour | Harness handling |
|---|---|---|---|
| `open()` on a path with a component that is a regular file (`afile/x`, `afile/../q`): `capture run --profiles`, the capture snapshot and manifest writes, `export-sql --prefix`, `write_sqlite_snapshot`, `write_html_report`, the scheduler state, `_load_json` | Linux: `NotADirectoryError: [Errno 20] Not a directory: '...'` (the kernel's `ENOTDIR`); Windows: `FileNotFoundError` (`winerror_to_errno` maps `ERROR_PATH_NOT_FOUND` to `ENOENT`, and `ERROR_DIRECTORY` to `ENOTDIR`) | The same: the runtime reports `ENOTDIR` as a missing directory on Unix, so `PythonOSError.Errno(exc, path)` asks `statx` for the kernel's errno of the path (`ENOTDIR`, `ENOENT`, `ELOOP`, ...); on Windows it maps the exception's Win32 `HResult` with CPython's `winerror_to_errno` table (`PythonOSError.WinErrorToErrno`) | Compared: `capture/registry-capture-r3/run-profiles-path-spellings`, `run-capture-id-through-missing-dir`, `export-sql-stems-and-column-case`; `PythonOSErrorTests` |
| `open()` on a directory (`capture run --profiles <dir>`, `capture compare` with a directory snapshot, `write_html_report`, `write_sqlite_snapshot`, `write_snapshot`, the scheduler state, `_load_json`, `profile.json`, a diff cache entry) | Linux: `IsADirectoryError: [Errno 21] Is a directory: '<dir>'` (the kernel's `EISDIR`); Windows: `PermissionError: [Errno 13] Permission denied: '<dir>'` (the CRT's `_wopen` gets `ERROR_ACCESS_DENIED` from `CreateFile` and maps it to `EACCES`; CPython 3.14 on the lab guest for `open`, `read_text` and `write_text`) | The same on each platform: every reader and writer raises `PythonOSError.DirectoryOpenErrno` (`EISDIR` on Unix, `EACCES` on Windows) before opening | Compared on Linux: `capture/registry-capture-r2/compare-current-is-directory`, `registry-capture-r3/run-profiles-path-spellings` (`p-dir-trailing`), `closeout-registry-capture-r1/run-profiles-through-links` (`p-dir-link`); every host: `PythonOSErrorTests.ReadingOrWritingADirectoryRaisesOpensErrorForThisPlatform` and the `OSErrorTexts.DirectoryOpen` assertions of the store, cache, report, SQL and capture tests |
| `registry_cli.main(argv)` argument parsing (`cli` records of `registry-scan` cases) | `argparse`: option abbreviation, `--option=value`, `-h`, a value beginning with `-` read as an option unless it matches `^-\d+$\|^-\d*\.\d+$`, `int()` / `float()` conversion errors and missing arguments all end in `SystemExit(2)` with usage on stderr | `ParityDump.RegistryCliLines` reads `--option value` pairs and one token; the parser itself is ported in phase 8 with the console tool | Restricted to argv both read identically: a subcommand first, then one positional token (none for `list-apps`) and `--option value` pairs from that subcommand's options spelled in full, no value or token beginning with `-` unless it is argparse's negative number (`^-\d+$\|^-\d*\.\d+$` over ASCII digits, read as a value or positional since no option looks like one: `search -5 --keyword hit`, `suggest-roots -.5`), `--max-depth` / `--max-hits` values `int()` accepts and `--time-budget` values `float()` accepts. `ParityDump.RequireRegistryCliSubset` refuses any other argv, so the dump exits with the reason and the compare fails visibly (`ParityDumpPhase7Tests`; `registry-scan/closeout-registry-capture-r1/cli-values-beginning-with-minus` holds the negative-number tokens and values) |
| A table whose `sqlite_master.sql` or `name` is stored as a BLOB (`PRAGMA writable_schema=ON`) | `build_sqlite_snapshot` keeps a BLOB `sql` as `bytes` in the snapshot, so only `json.dumps` (in `write_sqlite_snapshot`, `run_sql_export` after the destination is chosen, and the offline collector) raises `TypeError: Object of type bytes is not JSON serializable`, once every table is built, and any earlier error wins. `_iter_tables` is a generator: a BLOB `name` raises `TypeError: startswith first arg must be bytes or a tuple of bytes, not str` when the export reaches that row, after every text-named table before it | The same: `SnapshotTable.SchemaBytes` carries the bytes (`Schema` is null) and `Canonicaliser`'s JSON writer raises the `TypeError` for them; `SqliteSnapshots.IterTables` yields row by row; `run_sql_export`'s error handling leaves the `TypeError` to escape where Python's does | Compared: `sql-export/sql-r1/blob-schema`, `closeout-sql-reporting-r2/r2v-blob-schema-*`, `adv-blob-*`, `blob-schema-*`, `schema-name-blob-startswith-type-error`; `capture/closeout-sql-reporting-r2/adv-export-sql-blob-schema-point-of-failure`; `SqlOracleTests`, `SqlBlobSchemaTests` |
| Fix e inside `capture run` | `hunt_path(..., rules=default_rules())` with the double-escaped install-path pattern | `HuntRules.Default` with the single-escaped pattern | The Python dump runs with fix e applied (`PARITY_CAPTURE_FIX_E=1`, the default) and is compared exactly; a third dump with stock rules that differs from it counts the case as one expected divergence |
| `Failed to parse registry scan {path}: ...` / `Failed to parse snapshot {path}: ...` | The `JSONDecodeError` reason and position | `invalid JSON document` after the same prefix (`PythonJson` reports no reason), as the "Scheduling" row records for the schedule loaders | No harness case (no capture case feeds text that is not JSON); `CaptureRunnerOracleTests` normalises the reason (`json-decode-text`) |
| `capture run --sample-size` far past the detector guardrail (tens of gigabytes) | The detector clamps with its warning; the hunt's `handle.read(sample_size)` allocates the whole buffer first and raises `MemoryError` when the host cannot | The detector clamps with the same warning; the hunt reads what the file holds | Interpreter and host limit, not reproduced; no case (the outcome depends on the host's memory). `capture/capture-r1/hunt-glob-exclude-and-small-sample` compares a size past the guardrail that both read |
| A file the capture hunt cannot read | `PermissionError` escapes `hunt_path` and ends `run_capture` | Skipped and counted (fix b) | No harness case (committed trees are readable) |
| The Windows registry backend | `_WinRegBackend` over `winreg` | `WinRegistryBackend` with `WinRegistryValueConverter` (`Reg2Py`) | Not observable on the Linux harness: the fake backend stands in on both sides, its values given as the `winreg` value each registry type yields; `WinRegistryValueConverterTests` covers the conversion |
| A SQLite database opened for export | `sqlite3.connect(path)`, read-write: a hot journal beside the database is rolled back (the database file rewritten) and the rolled-back content exported | Read-only through a connection-string builder (plan decision 7), a directory refused before opening with the library's `SQLITE_CANTOPEN` text; a database with a hot journal is refused with `OperationalError: attempt to write a readonly database` and left as it is | Compared: `sql-export/sql-r1/directory-path` and the other refusal cases. No harness case builds a hot journal (its bytes depend on the library's page cache); `SqlSnapshotEdgeTests.AHotJournalIsRefusedRatherThanRolledBack` pins the port |
| A database path whose `str(Path)` is `:memory:` (Linux only: Windows names cannot hold `:`) | Once the existence check has passed, `sqlite3.connect(":memory:")` opens a new empty in-memory database: `tables: []` whatever the entry holds, a directory of that name included | The same (`SqliteSnapshots.Open`); an absolute path to such a file opens the file on both sides | Compared: `sql-export/sql-reporting-r1/memory-file-name`; `SqliteInMemoryNameTests` |
| `hash_salt` that is not a str (the harness's `null`) | Reaches Python only inside `f"{table}.{column}:{hash_salt}"`, so `None` salts with the text `None` | The typed API takes the salt as text | `ParityDump` passes `str(hash_salt)` (`None` for null) for a case value that is not a str, the text the f-string formats, and the records are compared exactly (`sql-export/sql-reporting-r1/hash-salt-null`); a placeholder that is not a str fails the port dump's cast, so such a case fails visibly |
| Report inputs nested past the interpreter's frame limit (about 1000 levels) reached by a per-level recursion: `redact_data` over `extra_metadata`, `profile_summary`, a diff mapping value or a hunt-hit value with a redactor active (the `html` and `json_lines` stages, and `manifest` for the metadata inputs), and `_json_safe` inside `summarise_metadata` (`core/types.py`) over a match's own `metadata` with or without a redactor (all five stages: `html`, `json_lines`, `json_lines_unsorted`, `summary`, `manifest`) | `RecursionError` from the recursion escapes `render_html_report`, `render_json_lines`, `summarise_detections` and `build_snapshot_manifest` | `RedactionFilter` and `DetectionMetadata.JsonSafe` walk on an explicit stack and every stage renders | Interpreter limit, not reproduced. `run_parity.sh report`: when the records differ, the case is run a second time in CPython with `PARITY_REPORT_RECURSION_LIMIT=20000` (`sys.setrecursionlimit`), and a stage stock Python ends with `RecursionError` where the port rendered is accepted only when the port's stage and its `key_order` child equal the raised-limit run's as canonical JSON text (`30` and `30.0`, `1` and `true` differ); the Python stage is then replaced by the port's before the byte comparison, one divergence per stage (`tools/parity/phase7_divergences.py`, whose `--self-test`, which includes a wrong port rendering and a number-type-only difference being refused, runs first); a stage the raised-limit run also fails, and every other difference, fails. `report/sql-reporting-r1/redaction-nesting-past-python-recursion-limit`; `closeout-sql-reporting-r1/deep-match-metadata-past-limit-no-redactor` (five stages), `redaction-deep-diff-mapping-stats-past-limit-html-only` (one), `redaction-deep-hunt-hit-excerpt-past-limit` (three). The same nesting the engine renders on both sides is compared exactly (`nesting-past-python-recursion-limit-unredacted`, `closeout-sql-reporting-r1/deep-legal-metadata-and-snapshot-extra-metadata-unredacted-exact`: `py_dump._key_order` and `_escape_lone_surrogates` describe and emit a record on an explicit stack) |
| The `TypeError` from sorting detection keys of mixed types in `compare_snapshots` (`added_keys`, `removed_keys`, `changed_keys`: `sorted(set(current) - set(baseline))`) | The operand types named in the message follow the set's iteration order: deterministic for `int` / `None` / `bool` keys (`snap/keys-int-none.json` and `keys-none-int.json` both say `'int' and 'NoneType'`), randomised str hashing for str keys | The same exception type; the operands follow the port's insertion order | The same class as the `diff_summary_snapshots` row under "Detection profiles". `run_parity.sh capture`: a `compare` step both sides end with that `TypeError` naming the same two types in opposite order takes the port's message, one divergence per step (`phase7_divergences.py`); every other difference fails. `capture/registry-capture-r1/compare-token-and-key-types` |
| `_load_snapshot` (`capture compare`) decoding | `path.read_text()` uses the locale encoding (UTF-8 on Linux and macOS, the ANSI code page on Windows outside UTF-8 mode), so a foreign snapshot holding non-ASCII UTF-8 gives mojibake keys or `UnicodeDecodeError` there; the snapshots `capture run` writes are ASCII (`ensure_ascii`) | Strict UTF-8 on every platform, with CPython's `UnicodeDecodeError` text, as the `_load_json` row under "Detection profiles" records | Not observable on the Linux harness |
| `Path.resolve()` on Windows (`capture run`'s root and every path under it, `--registry-scan` entries, `export-sql`'s output directory and database paths) | `ntpath.realpath`: the entry's stored letter case, 8.3 short names expanded, a mapped or subst drive replaced by what it maps, links followed by the OS, the longest prefix the OS can name joined with the rest as written, and the `\?\` prefix dropped when the input had none and the shorter spelling names the same path | The same (`PythonNtRealPath` over `WindowsNtPathSystem`: `CreateFileW` and `GetFinalPathNameByHandleW(VOLUME_NAME_DOS)`, the runtime's reparse reader for a link the OS cannot follow, a directory listing for a name access is refused to); on other platforms the physical walk of `PythonPath.ResolvePhysicalPath` | Not observable on the Linux harness; `PythonNtRealPathTests` runs the algorithm over a fake of the Windows calls on every host, `PythonPathTests.ResolveOnWindowsReportsTheStoredSpelling` runs it on Windows |
| `socket.gethostname()` on Windows (`capture.host`) | `GetComputerNameExW(ComputerNamePhysicalDnsHostname)`, a wide-character call | The same (`CaptureHostName`; `Dns.GetHostName` would call Winsock's ANSI `gethostname` and decode its bytes, changing a computer name outside ASCII) | Not observable on the Linux harness, where both call the resolver's `gethostname` |
| `registry_cli` `--max-depth` / `--max-hits` and `SearchSpec` limits | `argparse` `int`s of any size; `search_registry` clamps with `max(0, ...)` / `max(1, ...)`, and coerces `int(spec.max_depth)`, `int(spec.max_hits)` and `float(spec.time_budget_s)` itself, so a `SearchSpec` holding a value that does not convert counts as a call and an error in `registry_summary()` | `BigInteger` and `double` through `RegistryCommands`, `SearchSpec` and `OfflineRegistryScanSource`, coerced where the spec is built; the search clamps the same way and compares the result with counts, so a value past the long range behaves as `long.MaxValue`. `py_dump.py` hands `SearchSpec` the case's raw limits, so `ParityDump` builds the typed spec inside the instrumented call (`RegistryOperations.SearchRegistry(roots, Func<SearchSpec>, backend)`) and the usage counters match | Compared: `registry-scan/registry-capture-r1/cli-big-integers`, `registry-capture-r2/search-spec-none-limit`, `v2-search-limit-coercions-nan-inf` |
| `OSError` subclass names in the usage counters' `last_error` and error payloads | `errnomap`: `FileNotFoundError`, `PermissionError`, `FileExistsError`, `NotADirectoryError`, `IsADirectoryError`, `InterruptedError`, `BlockingIOError`, `BrokenPipeError`, `ChildProcessError`, `ProcessLookupError`, `ConnectionAbortedError`, `ConnectionRefusedError`, `ConnectionResetError`, `TimeoutError`, else `OSError` | The same over the Linux `errno` values (`PythonOSError.TypeName`) | Compared: `registry-scan/registry-capture-r1/oserror-types-in-usage` |
| A registry value that is a list holding `bytes` (only a `_Backend` other than `winreg` returns one; the harness fake does) | `str()` of the list spells `b'..'` reprs (`enumerate_installed_apps` display names, `_match_value` previews) | The same (`PythonRepr` spells a byte array as `bytes.__repr__` does) | Compared: `registry-scan/registry-capture-r2/v2-enumerate-sort-and-duplicate-values`, `v2-search-preview-full-text-and-list-bytes` |
| `capture run --hunt-glob` with an anchored pattern (`/etc/*`) | `NotImplementedError: Non-relative patterns are unsupported` from `Path.glob` | `PythonNotImplementedException` (a `NotSupportedException`) with the same text; the dumps name it `NotImplementedError` | Compared: `capture/registry-capture-r2/run-sample-size-and-globs` |
| The capture id's `%Y` (`datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")`) for a year below 1000 | glibc's `strftime` writes the year unpadded (`9990101T000000Z`, `10101T000000Z`); the Windows CRT's padding is not verified here | The year unpadded on every platform (`CaptureRunner.CaptureTimestamp`) | Compared: `capture/registry-capture-r2/v2-run-clock-year-padding-and-fractions` (a pinned clock; a real clock never reads below the year 1000) |
| `limit` of `build_sqlite_snapshot` | `limit <= 0` on the value as given (a str or list raises `TypeError: '<=' not supported ...`), then `f" LIMIT {int(limit)}"` for each exported table (`int(0.5)` is 0, a NaN raises `ValueError` and an infinity `OverflowError` only once a table is reached) | The same: `limit` is `object?` (`PythonValues.LessThanOrEqual`, `PythonBuiltins.Int` per table); product callers pass their `BigInteger?` | Compared: `sql-export/sql-reporting-r2/limit-half-float`, `limit-point-nine-nine`, `limit-string`, `limit-nan`, `limit-infinity`, `limit-negative-infinity`, `v2-limit-list` |
| A `tables` / `exclude_tables` entry that is not a str | `{name for name in tables or () if name}`: a truthy hashable non-str entry (an int) sits in the set and matches no table, a list raises `TypeError: unhashable type` | The typed lists hold str: `ParityDump.TableNames` yields a NUL-prefixed sentinel for such an entry (SQL text cannot name a table holding a NUL) and raises the `TypeError` where Python builds the set | Compared: `sql-export/sql-reporting-r2/tables-non-string-entry` |
| `write_sqlite_snapshot`, `write_html_report` (a path) and `write_snapshot` destinations that cannot be written | `IsADirectoryError` / `PermissionError` / `NotADirectoryError` with `open()`'s text | The same through `PythonTextFile.WriteText` (which also writes the capture files and the scheduler state), the errno from `PythonOSError.Errno(exc, path)` (see the `open()` row above) | No harness case on this surface (the dumps write to fixed destinations); `SqlSnapshotEdgeTests`, `ReportingEdgeTests`, `PythonOSErrorTests` |
| A read or write that fails once the file is open (`ENOSPC`, `EDQUOT`, `EIO`, `EFBIG`): the capture snapshot, manifest and SQL files, `capture compare` and registry scan reads, `_load_json`, the diff cache, the report, snapshot and scheduler state writes | `OSError` text without a file name (`[Errno 28] No space left on device`); only `open()` names the file | The same: `PythonTextFile` opens first (errors name the file) and reads or writes afterwards (errors do not); the runtime's `EFBIG` (`ArgumentOutOfRangeException` on Unix) is `[Errno 27] File too large` | Compared: `capture/closeout-registry-capture-r2/v2-write-errors-after-open` (links to `/dev/full`), `v2-read-errors-after-open` (links to `/proc/self/mem`); `PythonTextFileErrorTests` through the `PythonTextFile.OpenStream` seam |
| `Path(p).expanduser()` in `capture run --registry-scan` and `export-sql` (output directory and each database) for `~name` of an account the password database does not hold (or `~` with no home at all) | `RuntimeError: Could not determine home directory.` before anything is created (`os.path.expanduser` leaves the name, which still starts with `~`) | The same (`PythonPath.ExpandUser`; `PythonRuntimeException`). The offline collector's `os.path.expanduser` leaves the name, as Python's does | Compared: `capture/closeout-registry-capture-r2/expanduser-unknown-user`; `PythonPathExpandUserTests` |
| A path argument holding NUL (reachable from library and GUI callers; argv cannot carry NUL) | `ValueError` from the first call on the path: `open()`'s `embedded null byte` (`embedded null character` on Windows), `mkdir: embedded null character in path`, `lstat: embedded null character in path` from `resolve()` on posix; `Path.exists()` reads it as False | The same at the path helpers (`PythonOSError.ThrowIfEmbeddedNull` in `PythonTextFile`, `PythonPath.MakeDirectories` and, on posix, `PythonPath.Resolve`). The Windows texts are unverified until the phase 11 lab campaign | Compared: `capture/closeout-registry-capture-r2/nul-in-path-arguments`; `PythonTextFileErrorTests` |
| A `stat` error `Path.exists()` / `is_dir()` does not ignore on Linux (`ENAMETOOLONG`) | `OSError` of the errno's `errnomap` class | `UnixFileType.Stat` raises Python's text with the errno as `HResult` (on the inner exception of the `UnauthorizedAccessException` for `EACCES`), which the error name mappers read | Compared: `capture/closeout-registry-capture-r2/name-too-long-everywhere`; `PythonTextFileErrorTests` |
| A write through a final symlink whose target ends in `/`, resolves under an existing parent and is not an existing directory (`out/c-snapshot.json -> newdir/`, `-> ../afile/`, `-> /proc/version/`): the capture snapshot and manifest, the `export-sql` snapshot and manifest | The kernel's `open(O_CREAT)` fails with `EISDIR`: `IsADirectoryError: [Errno 21] Is a directory: '<path>'` | The runtime reports that `EISDIR` as access denied, so the port raises `PermissionError: [Errno 13] Permission denied: '<path>'`; nothing is written on either side. Reach: a link spelled with a trailing slash at the exact destination name | OS error text at an extreme, recorded, not reproduced. `run_parity.sh capture`: a step both sides end with those two errors for the same path, which must name a symlink in the case's committed `workdir/` whose target ends in `/`, has an existing parent directory and is not itself an existing directory, takes the port's error, one divergence per step (`phase7_divergences.py`, with a self-test); every other difference fails. `capture/closeout-registry-capture-r2/v2-dangling-final-link-writes`, `v2-export-sql-dangling-destinations`, `capture/final-r1/trailing-slash-links-to-existing-non-directories` |
| `Path.exists()` on Windows for a stat error outside the ignored set (errno 2, 20, 9, 40; winerror 21, 123, 1921), such as `ERROR_ACCESS_DENIED` for a path inside another user's profile (`capture compare`, `export-sql`'s database check, `capture run`'s root check and `--registry-scan` entries) | `os.stat` raises (`win32_xstat_slow_impl` restores the access-denied error after its `FindFirstFileW` fallback fails), so `exists()` raises `PermissionError: [WinError 5]` out of the command, or into its error handler | `RunProfileStore.Exists` falls back to `File.Exists` / `Directory.Exists` off Linux, which never raise: the path reads as missing (`current snapshot not found`, `database not found`, `registry scan file not found`) | Unverified on Windows until the phase 11 lab campaign; not observable on the Linux harness, where `statx` raises as Python does (`capture/closeout-registry-capture-r2/v2-paths-under-unsearchable-directory`) |
| `profile_summary`, `extra_metadata` and `legal_metadata` given as a sequence of pairs, or a mapping diff given so (`dict(x)`, `dict.update(x)`) | Accepted as a dict; a pair whose key is not a str (`[[1, 2]]`) gives `{1: 2}`; a sequence that is not pairs raises `dict()`'s `TypeError` / `ValueError`; a truthy scalar `TypeError: '<type>' object is not iterable` | The adapters take string-keyed mappings: `ParityDump` applies `PythonBuiltins.Dict` in the order Python reaches each input, and `HtmlReport.SerialiseDiff` applies it to a diff, with Python's errors; a pair whose key is not a str raises `TypeError: <input> keys must be str, not '<type>'` (the decision the detection profile `metadata` row records) | Compared: `report/sql-reporting-r2/diff-list-of-pairs`, `diff-list-of-ints`, `diff-string-value`, `profile-summary-list-of-pairs`, `extra-metadata-list-of-pairs`, `v2-profile-summary-string`; no case holds a non-str key |
| A `DetectionMatch` whose `metadata` is not a mapping, or a `warnings` entry that is not a str | `summarise_metadata` raises `MetadataValidationError` when an adapter reaches the match; the warning block raises `AttributeError: '<type>' object has no attribute 'startswith'` after every payload is prepared | The typed match and `IEnumerable<string>` cannot hold them: `ParityDump` hands the adapters sequences that raise the same errors where Python reaches them | Compared: `report/sql-reporting-r2/match-metadata-not-a-mapping`, `v2-warnings-non-string-entry` |

## GUI-only behaviour (no Python oracle)

| Behaviour | Port | Harness handling |
|---|---|---|
| Relative scan roots and the legacy diff cache (`DriftbusterBackend.RunServerScansAsync`) | The scan runs in process, so a relative root resolves against the process working directory, as every other facade call resolves paths. The Python facade started `python -m driftbuster.multi_server` with its working directory set to the checkout it found above the working directory, the application base or the process directory (by `pyproject.toml` or `src/driftbuster/multi_server.py`), so relative roots resolved there. The legacy `<checkout>/artifacts/cache/diffs` migration now finds the checkout by `DriftBuster.sln`, which sits beside `pyproject.toml` at the repository root; `scripts/release_build.py` never packaged `src/driftbuster`, so no shipped build had a checkout at its application base to migrate from. The facade's cache directory goes through `DiffCache.ResolveCacheDirectory` as an explicit directory, the `_resolve_cache_dir(cache_dir)` branch the bridge reached (user home expanded, created, symlinks and `..` followed as the kernel follows them), and the migration copies into the directory the kernel reaches; `MultiServerCacheDirectoryTests` covers a data root through a symlink and an `XDG_DATA_HOME` with `..` after one. The GUI cannot send a relative root (`ServerSelectionViewModel.ValidateRoot`: "Path must be absolute.") | Not compared (no Python oracle for the facade). `DriftbusterBackendTests.RunServerScansAsync_executes_multi_server_runner` addresses the fixtures from the repository root |
| Run profiles through the facade (`ListProfilesAsync`, `SaveProfileAsync`, `RunProfileAsync`) | Delegate to `RunProfileStore` and `RunProfileExecutor`, so a GUI run now copies every file through `copy_with_secret_filter` and writes Python's `metadata.json` (with `secrets`). The facade keeps its own checks: a blank profile name raises `InvalidOperationException("Profile name is required.")` before anything is validated or written, a blank `baseDir` means the working directory, and `saveProfile: false` validates the profile as `save_profile` does without writing `profile.json`. `list_profiles` raises on a `profile.json` that is not JSON (and `IsADirectoryError` on a directory named `profile.json`), where the facade used to skip it. `RunProfile.FromDefinition` reads an empty baseline as none, and the model drops a blank alias as the `profile.json` reader does. The Profiles tab saves the sources in their declared order (a structured profile collects in that order and names an aliasless source by its position), with no baseline when the loaded profile had none and the first source is still the baseline, and saves a loaded name, description, option key and secret scanner lists exactly as loaded while they are unedited (an edited name or option key is stripped with `str.strip`, option keys are compared exactly and a later row with the same key wins), so a loaded profile saved without edits collects exactly as loaded (`RunProfilesViewModelTests.A_loaded_profile_saved_without_edits_collects_exactly_as_loaded`). `RunProfileAsync` runs a structured profile with the option values its stored `profile.json` holds, as `load_profile` reads them (a list stays a list for `build_context`), for every key whose model text is still that value's `str()` text, read before the run saves over the file; a new or edited option runs with its text (`DriftbusterBackendEdgeTests.RunProfile_runs_a_stored_structured_profile_with_its_raw_option_values`). It saves a loaded source's path and alias exactly as loaded
while they are unedited (so an alias with surrounding white space keeps naming its run directory); an edited path or alias is stripped with
Python's `str.strip` (`PythonText.Strip`), and an alias that leaves empty is none. The baseline is saved as its source's saved path and is matched exactly (not ignoring case) when a profile loads, so it is always one of the saved sources (`A_loaded_baseline_saves_as_the_spelling_of_its_source`); a source's existence check uses that saved path too. The Profiles tab lets an optional source name a path that does not exist, flags two sources whose aliases name the same run directory (ignoring case), and edits exclude patterns one per line, each kept as written, saving loaded patterns unchanged while their text is unchanged; the offline collector package (`OfflineCollectorWriter`) writes them as written too, so a profile excludes the same files when run and when collected. The model's `SecretScannerOptions` holds only the two ignore lists: `RunProfile.FromDefinition` writes each list that is not empty, so a profile saved by Python with other `secret_scanner` keys (an inline `ruleset`) or with an empty list loses them when the GUI loads and saves it again. `RunProfileRunResult` carries no `secrets`; they are in `metadata.json` | Not compared (no Python oracle for the facade) |
| Schedule cards through the facade (`ListSchedulesAsync`, `SaveSchedulesAsync`) | `ScheduleStore.ListSchedules` / `SaveSchedules`, the facade's reader and writer. The manifest is read as `_load_schedule_payload` reads it (`LoadSchedulePayload`, over `PythonJson`: its messages for text that is not JSON or a payload that is not an array, and the decoder's limits), so every manifest the scheduler reads loads as cards, whatever its nesting, `NaN` / `Infinity` or unpaired surrogates. Cards are trimmed, tags de-duplicated ignoring case, entries without a name, profile or interval skipped on load (the scheduler refuses the whole manifest for those), and cards kept in manifest order (the scheduler registers schedules in that order, which orders runs due together); `schedules.json` is written as `json.dumps({"schedules": [...]}, indent=2)` plus a new line, except that a container nested 64 levels deep or more is written on one line as without `indent` (the indented layout of a manifest nested thousands of levels deep, which the scheduler still reads, grows with the square of the depth; Python's pure-Python `indent` encoder would raise `RecursionError` near 1000 levels instead) (`ScheduleStoreTests.GuiSaveWritesDeepNestingOnOneLine`). A card shows each field as the `str()` text `ScheduleSpec.from_dict` reads (`null` as `None`, `1e2` as `100.0`, a list or object tag as its `repr`), a metadata value as its JSON text (a bool as `True` / `False`, null as nothing), and a falsy `start_at` as no start. Each card keeps the entry it was read from (`ScheduleDefinition.ManifestEntry`): a field whose card text still shows what the entry held is written back as the entry held it (JSON value and type, surrounding whitespace, a `null` time zone, metadata keys that differ only by whitespace), keys the card does not show are kept in place, and only an edited field is written from the card (`every` and metadata values also keep their JSON value through `EveryValue` / `MetadataValues` while their text is unchanged). A number is written as `json.dumps` spells the value `json.loads` read (`1e2` as `100.0`), so a load and an unchanged save leaves what the scheduler reads, and the order of runs due together, unchanged, with one exception: a manifest that is a bare top-level array is saved as `{"schedules": [...]}`, one container deeper, so a bare-array manifest nested to the decoder's limit (9998 containers) is 9999 deep after the save and both the scheduler and the tab's next load refuse it with `RecursionError`; an object manifest at the limit round-trips unchanged (recorded: the tab always writes the object form; `ScheduleStore.Manifest.cs` notes the exception) (`ScheduleStoreTests.GuiLoadAndSaveLeavesWhatTheSchedulerReads`, `GuiCardsLoadEveryManifestTheSchedulerReads`, `GuiLoadAndSaveKeepsTheOrderOfRunsDueTogether`). Saving runs every written entry through `ScheduleSpec.FromDict` and then registers them in order with a `ProfileScheduler` (a repeated name, a first run past the date range), the checks `run_profiles_cli` makes when it loads the manifest; a card Python would reject is refused with Python's message, which the GUI shows on the card (`ScheduleStore.ValidationError`). The Profiles tab asks for both window times and reads a window without a time zone as UTC, as `ScheduleWindow.from_dict` does. It writes the cards only while they are the manifest as last read: before the first load, and after a load that raised (a manifest the scheduler refuses: not JSON, past the decoder's limits), saving or running a profile leaves `schedules.json` untouched, clears the cards and says that cards were not saved (`RunProfilesViewModelTests.Schedules_are_not_saved_over_a_manifest_that_did_not_load`) | Not compared (no Python oracle for the facade) |
| Scheduler commands through the facade (`ListScheduleStatusAsync`, `ListDueSchedulesAsync`, `CompleteScheduleAsync`, `SkipScheduleAsync`) | `ScheduleCommands` payloads as models; a blank base directory, manifest path or state path means the default, and metadata values keep their JSON types | Not compared (no Python oracle for the facade) |
| Diff planner file decoding (`DriftbusterBackend.DiffAsync`) | Reads each file with `StreamReader(UTF-8, detectEncodingFromByteOrderMarks: true)`: a UTF-8, UTF-16 or UTF-32 byte order mark selects the decoder, otherwise UTF-8 with replacement. Python's nearest reader, `multi_server._read_text`, decodes bytes as UTF-8 with replacement, so a UTF-16 file would become U+FFFD and NUL-interleaved text there. The GUI planner has no Python counterpart, so it keeps the BOM-aware reader (`DiffAsync_decodes_files_by_their_byte_order_mark`) | Not compared. The `diff` and `canon` parity surfaces never use the planner's reader: both dumps read bytes as UTF-8 with replacement (`py_dump._read_replace`, `ParityDump.ReadReplace`), pinned by `Diff_reads_a_utf16_file_as_utf8_with_replacement` |

## Platform limits

| Case | Python behaviour | Port behaviour | Harness handling |
|---|---|---|---|
| A Linux file or directory name that is not valid UTF-8 (bytes such as `0xFF`) | `os.scandir` keeps the bytes through `surrogateescape` (`'\udcff'`), so the walk yields a file and every surface opens and scans it, and walks into a directory and scans its children; a run profile's `glob.glob` and directory walk list it and copy it | .NET decodes listed names as UTF-8 with U+FFFD for each invalid sequence; the decoded name names nothing on disk, or names a sibling whose name really holds U+FFFD. The port never opens a guessed name: `PythonPath.IsUndecodableName` (U+FFFD in the name and a failing `lstat`) marks the entry, `Detector.ScanPath` reports it through `HandleError` (`DetectorIOException`, "File name is not valid UTF-8") and `HuntEngine.HuntPath` lists it in `UnreadableFiles`. A directory is marked the same way (the entry itself, listed as if it were a file) and its whole subtree is neither walked nor reported: its children cannot be named either. Run profiles neither report nor copy it: `PythonModuleGlob` lists a name holding U+FFFD only when an entry of that spelling exists, and once, and the collectors' directory walks skip such a path when it names no entry and every repeat of a path holding U+FFFD, so a U+FFFD sibling is copied once and never in the undecodable entry's place (`RunProfileCollectionLimitsTests`) | No committed case (a committed name must be valid UTF-8 for git on every platform); `PythonPathTests`, `DetectorSpecialEntriesTests` and `HuntWalkEntriesTests` create one on Linux, and the multi-server case `runtime-r1/undecodable-name` generates one (see "Multi-server surface"). DriftBuster ships for Windows first, where names are UTF-16 |
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

`detect` and `decode` can show only fix c, the interpreter-limit cases, the SQLite URI names and the unreadable-root case. `diff` and `canon` add fix d (and `diff` the unpaired-surrogate interpreter error), `hunt` adds
fixes e and b, `secrets` adds fix g, `run-profile` adds fix g, the structured sources decision and the surrogate name lookups of decision R, `profile-store` the text fields stored as
`str()`, `schedule` the `JSONDecodeError` text and the call budget at the nesting limit, `multi-server` adds fixes a, b and d, the undecodable-name platform limit, the unpaired-surrogate raises and the `diff_summary` port-model limit, `capture` adds fix e, the mixed-type key sort `TypeError` operand order and `EISDIR` through a trailing-slash link, and `report` the `RecursionError` stages of a redaction nested past the interpreter's frame limit. `sql-export` and `registry-scan` show none. `detect` without `--plugins` runs the full default registry on both sides
(all eleven plugins in Python priority order). Any other difference is a port defect.
