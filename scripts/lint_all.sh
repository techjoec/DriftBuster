#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"

python_bin="${repo_root}/.venv/bin/python"

echo "[lint] python compileall"
"${python_bin}" -m compileall "${repo_root}/src"

echo "[lint] pycodestyle"
"${python_bin}" -m pycodestyle "${repo_root}/src"

echo "[lint] powershell"
pwsh "${repo_root}/scripts/lint_powershell.ps1"

echo "[lint] dotnet format backend"
dotnet format "${repo_root}/gui/DriftBuster.Backend/DriftBuster.Backend.csproj" --verify-no-changes --verbosity minimal

echo "[lint] dotnet format gui"
dotnet format "${repo_root}/gui/DriftBuster.Gui/DriftBuster.Gui.csproj" --verify-no-changes --verbosity minimal

echo "[lint] dotnet format gui tests"
dotnet format "${repo_root}/gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj" --verify-no-changes --verbosity minimal

echo "[lint] complete"
