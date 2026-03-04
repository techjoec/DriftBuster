#!/usr/bin/env bash
set -euo pipefail
# Unified test runner for Claude Code: handles __JOE_PROFILE_ENV guard,
# runs Python + .NET tests with coverage gates, prints summary.
#
# Usage: ./scripts/test-all.sh

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

FAILURES=0

# Activate local venv if present
if [[ -f ".venv/bin/activate" ]]; then
    # shellcheck disable=SC1091
    source .venv/bin/activate
fi

# Helper: run dotnet in a clean shell (handles __JOE_PROFILE_ENV guard)
run_dotnet() {
    unset __JOE_PROFILE_ENV 2>/dev/null || true
    bash --login -c "dotnet $*"
}

echo "=== Python tests (90% coverage gate) ==="
if coverage run --source=src/driftbuster -m pytest -q; then
    if coverage report --fail-under=90; then
        coverage json -o coverage.json
        echo "PASS: Python tests + coverage"
    else
        echo "FAIL: Python coverage below 90%"
        FAILURES=$((FAILURES + 1))
    fi
else
    echo "FAIL: Python tests"
    FAILURES=$((FAILURES + 1))
fi

echo ""
echo "=== Python compliance guardrail ==="
if python -m scripts.coverage_watch --python-json coverage.json 2>/dev/null; then
    echo "PASS: Compliance watch"
else
    echo "FAIL: Compliance watch"
    FAILURES=$((FAILURES + 1))
fi

echo ""
echo "=== Python lint (ruff) ==="
if ruff check src; then
    echo "PASS: ruff"
else
    echo "FAIL: ruff"
    FAILURES=$((FAILURES + 1))
fi

echo ""
echo "=== .NET tests (90% line coverage gate) ==="
if run_dotnet test gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj \
    -p:Threshold=90 -p:ThresholdType=line -p:ThresholdStat=total \
    --collect:\"XPlat Code Coverage\" \
    --results-directory artifacts/coverage-dotnet -v minimal; then
    echo "PASS: .NET tests"
else
    echo "FAIL: .NET tests"
    FAILURES=$((FAILURES + 1))
fi

echo ""
echo "=== .NET scoped coverage guard (90%) ==="
coverage_report_log="$(mktemp)"
if python -m scripts.coverage_report --dotnet-threshold 90 --enforce-dotnet-threshold >"${coverage_report_log}" 2>&1; then
    cat "${coverage_report_log}"
    echo "PASS: .NET scoped coverage"
else
    cat "${coverage_report_log}"
    echo "FAIL: .NET scoped coverage below 90%"
    FAILURES=$((FAILURES + 1))
fi
rm -f "${coverage_report_log}"

echo ""
echo "=== .NET format check ==="
for proj in gui/DriftBuster.Backend/DriftBuster.Backend.csproj \
            gui/DriftBuster.Gui/DriftBuster.Gui.csproj \
            gui/DriftBuster.Gui.Tests/DriftBuster.Gui.Tests.csproj; do
    if run_dotnet format "$proj" --verify-no-changes; then
        echo "PASS: dotnet format $proj"
    else
        echo "FAIL: dotnet format $proj"
        FAILURES=$((FAILURES + 1))
    fi
done

echo ""
echo "========================================"
if [[ $FAILURES -eq 0 ]]; then
    echo "ALL CHECKS PASSED"
    exit 0
else
    echo "FAILED: $FAILURES check(s)"
    exit 1
fi
