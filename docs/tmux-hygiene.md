# tmux Hygiene Checklist

To keep the Codex CLI environment tidy when running long-lived tests or builds,
follow this checklist before and after launching background sessions.

## Before starting a session
1. Run `scripts/tmux_hygiene.sh --list` to view existing sessions.
2. If you find stale sessions that belong to the current Codex CLI process,
   prune them with `scripts/tmux_hygiene.sh --prune-prefix` or target specific
   sessions via `--kill <name>`.
3. Record the session name using the `codexcli-<pid>-<tag>` pattern.

## During the session
- Keep long-running commands (smoke tests, soak benchmarks) inside tmux so they
  survive terminal disconnects.
- Capture benchmark results in `artifacts/benchmarks/<timestamp>-<topic>.md`
  using the template provided in `artifacts/benchmarks/README.md`.

## After finishing
1. Save any relevant logs or metrics to the benchmark entry you created.
2. Kill the tmux session with `scripts/tmux_hygiene.sh --kill <name>`.
3. Run `--list` again to confirm the environment is clean.

## Automation tips
- Set `SESSION_PREFIX=codexcli-<pid>-` when running the script to scope the
  prune action to the active CLI process.
- Add `alias tmuxh="scripts/tmux_hygiene.sh"` to your shell profile for quick
  access.
