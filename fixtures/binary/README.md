# Binary Format Fixtures

Synthetic samples supporting the binary/hybrid format adapters:

- `settings.sqlite` — SQLite database populated with anonymous configuration
  keys. Created with `sqlite3` and contains no production data.
- `preferences.plist` — Binary property list generated via Python's
  `plistlib` with placeholder values.
- `config_frontmatter.md` — Markdown document containing YAML front matter used
  to exercise hybrid detection paths.

Hashes and sizes live in `MANIFEST.json`. Legal review for these fixtures is
tracked in `docs/legal-safeguards.md` under **Binary fixtures**.
