# Fixture sanitisation workflows

## Dotenv (`.env*`) scrub pipeline

1. Copy raw dotenv files into a temporary working directory outside the repo.
2. Run the sanitiser snippet below to replace secrets with deterministic placeholders while retaining structural cues:

   ```bash
   python - <<'PY'
   import os
   import re
   from pathlib import Path

   source = Path(os.environ.get("DOTENV_SOURCE", "./incoming"))
   target = Path(os.environ.get("DOTENV_TARGET", "./sanitised"))
   target.mkdir(parents=True, exist_ok=True)

   replacement_map = {
       re.compile(r"(?i)password|secret|token|key"): "REDACTED_SECRET",
       re.compile(r"(?i)url"): "https://redacted.local/service",
       re.compile(r"(?i)user|username"): "service_account",
   }

   for path in source.glob("*.env*"):
       text = path.read_text(encoding="utf-8", errors="ignore")
       for pattern, replacement in replacement_map.items():
           text = pattern.sub(replacement, text)
       target_path = target / path.name
       target_path.write_text(text, encoding="utf-8")
       print(f"sanitised {path.name} -> {target_path.relative_to(Path.cwd())}")
   PY
   ```

3. Review the sanitised output for environment-specific details (hostnames, tenant IDs) and replace them with representative placeholders if required.
4. Place the scrubbed file under `fixtures/` and keep a short provenance note referencing this README.

## Audit linkage
- Detection metadata now emits an `env-sanitisation-workflow` remediation entry pointing back to this README so reviewers can trace how shared fixtures were prepared.
- Commit messages should highlight when new fixtures are produced via this workflow.
