# Offline Runner Samples

JSON payloads in this folder showcase how the portable PowerShell runner
is configured.  The `profiles` directory contains plain profile objects
while `configs` wrap those profiles with runner settings and metadata for
offline collection scenarios.

## Pattern syntax

- Source `path` values are matched one path segment at a time: `**` stands for
  zero or more directory levels, `*` matches any run of characters within one
  segment (never a `/`), `?` matches one character, and linked directories are
  not descended into.
- `exclude` entries use the same wildcards, but there `*` matches any run of
  characters with a `/` included.
- In both, every other character — brackets included — is literal, and matching
  ignores case on Windows only, where `\` reads as `/` in both the pattern and
  the path. An `exclude` entry matches a collected file when it matches the scan-relative path or the
  file name, so `*.bak` drops every `.bak` anywhere under the source.
- Secret rule `pattern` values and `ignore_patterns` are .NET regular
  expressions (`System.Text.RegularExpressions` syntax), compiled
  culture-invariant. A rule is case-sensitive unless its `flags` contain `i` or
  the pattern itself sets `(?i)`. A rule `pattern` that does not parse stops
  the run with a config error; an `ignore_patterns` entry that does not parse is
  skipped.

## Strict reading

The runner reads its config (and the encryption keyset) strictly: every key
must be one the runner knows, spelt in the same case, with a value of the right
type. Anything else stops the run before collection starts, with an error that
names the file and the JSON path:

```text
C:\collect\app.config.json: $.profile.sources[0].exlude: unknown key
```

Each source is an object: `{"path", "alias", "optional", "exclude"}`, or one
with a `registry_scan` or `sql_snapshot` block. A source without an `alias` is
collected into `data/source_NN` (its position, from `00`).
