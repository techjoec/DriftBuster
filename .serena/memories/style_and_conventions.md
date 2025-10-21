# Style and Conventions
- Python: follow `pycodestyle` with 140-character limit (`python -m pycodestyle src`); prioritize readable functional blocks, add comments sparingly for non-obvious logic.
- Maintain ≥90% line coverage for any touched Python module (`src/driftbuster`) and for .NET surfaces; add focused tests alongside new format plugins per `docs/plugin-test-checklist.md`.
- .NET: C#/Avalonia projects target net8.0 with nullable + implicit usings enabled (`Directory.Build.props`); run `dotnet format ... --verify-no-changes` for backend, GUI, and tests to satisfy analyzer-enforced style.
- Provenance matters: contributions must be Apache-2.0 compatible and original (see `CONTRIBUTING.md`), with provenance comments if derived from public behavior.
- Plugins should register via `driftbuster.formats.register` and document format support updates in `docs/` (e.g., `docs/format-support.md`).
- Avoid adding GitHub Actions or automation hooks; all checks stay local and documented.