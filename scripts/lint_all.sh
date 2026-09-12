#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "${repo_root}"

# dotnet lives behind the login profile; __JOE_PROFILE_ENV blocks .profile in child shells.
run_dotnet() {
  unset __JOE_PROFILE_ENV 2>/dev/null || true
  bash --login -c "dotnet $*"
}

echo "[lint] python compileall"
python -m compileall -q src

echo "[lint] ruff"
ruff check src tests scripts

echo "[lint] powershell"
pwsh scripts/lint_powershell.ps1

echo "[lint] dotnet format backend"
run_dotnet format gui/DriftBuster.Backend/DriftBuster.Backend.csproj --verify-no-changes --verbosity minimal

echo "[lint] dotnet format gui"
run_dotnet format gui/DriftBuster.Gui/DriftBuster.Gui.csproj --verify-no-changes --verbosity minimal

echo "[lint] dotnet format gui tests"
run_dotnet format gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj --verify-no-changes --verbosity minimal

echo "[lint] dotnet format backend tests"
run_dotnet format gui/DriftBuster.Backend.Tests/DriftBuster.Backend.Tests.csproj --verify-no-changes --verbosity minimal

echo "[lint] dotnet format cli"
run_dotnet format cli/DriftBuster.Cli/DriftBuster.Cli.csproj --verify-no-changes --verbosity minimal

echo "[lint] dotnet format cli tests"
run_dotnet format cli/DriftBuster.Cli.Tests/DriftBuster.Cli.Tests.csproj --verify-no-changes --verbosity minimal

echo "[lint] complete"
