#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "${repo_root}"

# dotnet may live behind the login profile; __JOE_PROFILE_ENV blocks .profile in child shells.
if ! command -v dotnet >/dev/null 2>&1; then
  PATH="$(env -u __JOE_PROFILE_ENV bash --login -c 'printf %s "$PATH"')"
  export PATH
fi
export AVALONIA_TELEMETRY_OPTOUT=1

echo "[lint] dotnet format solution"
dotnet format DriftBuster.sln --verify-no-changes --verbosity minimal

echo "[lint] powershell"
pwsh -NoProfile -File scripts/lint_powershell.ps1

echo "[lint] complete"
