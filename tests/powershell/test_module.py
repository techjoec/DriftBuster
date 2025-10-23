from __future__ import annotations

import json
import shutil
import subprocess
from pathlib import Path

import pytest


pytestmark = pytest.mark.skipif(
    shutil.which("pwsh") is None, reason="PowerShell (pwsh) is not available"
)


MODULE_PATH = Path("cli/DriftBuster.PowerShell/DriftBuster.psm1")


def _ps_literal(text: str) -> str:
    return f"'{text.replace("'", "''")}'"


@pytest.fixture(scope="session")
def published_backend() -> Path:
    subprocess.run(
        [
            "dotnet",
            "publish",
            "gui/DriftBuster.Backend/DriftBuster.Backend.csproj",
            "-c",
            "Debug",
            "-o",
            "gui/DriftBuster.Backend/bin/Debug/published",
        ],
        check=True,
        capture_output=True,
        text=True,
    )
    return MODULE_PATH.resolve()


def _run_powershell(module_path: Path, body: str) -> subprocess.CompletedProcess[str]:
    module_literal = _ps_literal(str(module_path))
    script = f"$module = {module_literal}; Import-Module $module -Force; {body}"
    return subprocess.run(
        ["pwsh", "-NoLogo", "-NoProfile", "-Command", script],
        check=True,
        capture_output=True,
        text=True,
    )


def _capture_json(module_path: Path, body: str, *, depth: int = 6) -> object:
    command = f"{body} | ConvertTo-Json -Depth {depth}"
    completed = _run_powershell(module_path, command)
    output = completed.stdout.strip()
    if not output:
        raise AssertionError("PowerShell command produced no output")
    return json.loads(output)


def test_ping_returns_pong(published_backend: Path) -> None:
    payload = _capture_json(published_backend, "$result = Test-DriftBusterPing; $result")
    assert payload["Status"] == "pong"


def test_diff_pair_round_trip(published_backend: Path, tmp_path: Path) -> None:
    body = """
    $left = New-TemporaryFile
    $right = New-TemporaryFile
    Set-Content -LiteralPath $left 'alpha' -NoNewline
    Set-Content -LiteralPath $right 'beta' -NoNewline
    $result = Invoke-DriftBusterDiff -Left $left -Right $right
    $result
    """
    payload = _capture_json(published_backend, body, depth=8)

    assert payload["comparisons"]
    comparison = payload["comparisons"][0]
    assert comparison["plan"]["before"].startswith("alpha")
    assert comparison["plan"]["after"].startswith("beta")


def test_run_profile_creates_artifacts(published_backend: Path) -> None:
    body = """
    $base = New-TemporaryFile
    Remove-Item -LiteralPath $base -Force
    New-Item -ItemType Directory -Path $base | Out-Null
    $source = Join-Path $base 'sources'
    New-Item -ItemType Directory -Path $source | Out-Null
    $baseline = Join-Path $source 'baseline.txt'
    $data = Join-Path $source 'data.txt'
    Set-Content -LiteralPath $baseline 'baseline'
    Set-Content -LiteralPath $data 'data'
    $profile = [DriftBuster.Backend.Models.RunProfileDefinition]::new()
    $profile.Name = 'Profile One'
    $profile.Baseline = $baseline
    $profile.Sources = @($baseline, ($source + '/*.txt'))
    $result = Invoke-DriftBusterRunProfile -Profile $profile -BaseDir $base
    $result
    """

    payload = _capture_json(published_backend, body, depth=6)

    assert payload["Files"]
    assert any(entry["Destination"].endswith("baseline.txt") for entry in payload["Files"])
