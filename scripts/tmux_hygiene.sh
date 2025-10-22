#!/usr/bin/env bash
set -euo pipefail

if ! command -v tmux >/dev/null 2>&1; then
  echo "tmux_hygiene: tmux is not installed or not on PATH" >&2
  exit 1
fi

SESSION_PREFIX=${SESSION_PREFIX:-"codexcli-"}

list_sessions() {
  if tmux ls >/dev/null 2>&1; then
    tmux ls
  else
    echo "No tmux sessions present."
  fi
}

kill_session() {
  local target="$1"
  if [[ -z "$target" ]]; then
    echo "tmux_hygiene: session name required for --kill" >&2
    exit 2
  fi
  tmux kill-session -t "$target"
  echo "Killed session: $target"
}

usage() {
  cat <<USAGE
Usage: tmux_hygiene.sh [--list] [--kill SESSION] [--prune-prefix]

Options:
  --list             List all tmux sessions (default action when no options).
  --kill SESSION     Kill the specified tmux session.
  --prune-prefix     Kill all sessions whose names start with \"$SESSION_PREFIX\".
  -h, --help         Show this help message.

Environment:
  SESSION_PREFIX     Prefix used when pruning sessions (default: codexcli-).
USAGE
}

prune_prefix() {
  local sessions
  sessions=$(tmux ls 2>/dev/null | awk -F: -v p="$SESSION_PREFIX" '$1 ~ "^" p {print $1}') || true
  if [[ -z "$sessions" ]]; then
    echo "No sessions found with prefix '$SESSION_PREFIX'."
    return
  fi
  while IFS= read -r session; do
    tmux kill-session -t "$session"
    echo "Killed session: $session"
  done <<<"$sessions"
}

if [[ $# -eq 0 ]]; then
  list_sessions
  exit 0
fi

while [[ $# -gt 0 ]]; do
  case "$1" in
    --list)
      list_sessions
      shift
      ;;
    --kill)
      kill_session "$2"
      shift 2
      ;;
    --prune-prefix)
      prune_prefix
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "tmux_hygiene: unknown argument '$1'" >&2
      usage >&2
      exit 2
      ;;
  esac
done
