from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
from pathlib import Path
from typing import Any

import pytest


def _pwsh_runtime_version() -> tuple[int, ...]:
    """Return the .NET runtime version PowerShell is running on."""
    if shutil.which("pwsh") is None:
        return (0,)
    try:
        result = subprocess.run(
            ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
             "[System.Environment]::Version.ToString()"],
            capture_output=True, text=True, timeout=15,
        )
        parts = result.stdout.strip().split(".")
        return tuple(int(p) for p in parts if p.isdigit())
    except Exception:
        return (0,)


_PWSH_RUNTIME = _pwsh_runtime_version()

pytestmark = [
    pytest.mark.skipif(
        shutil.which("pwsh") is None, reason="PowerShell (pwsh) is not available"
    ),
    pytest.mark.skipif(
        _PWSH_RUNTIME < (10,),
        reason=f"Backend targets net10.0; PowerShell runtime is .NET {'.'.join(str(v) for v in _PWSH_RUNTIME)} (need >=10)",
    ),
]


MODULE_PATH = Path("cli/DriftBuster.PowerShell/DriftBuster.psm1")


def _ps_literal(text: str) -> str:
    return f"'{text.replace("'", "''")}'"


_ANSI_ESCAPE = re.compile(r"\x1b\[[0-9;]*m")


def _strip_ansi(text: str) -> str:
    return _ANSI_ESCAPE.sub("", text)


@pytest.fixture(scope="session")
def backend_data_root(tmp_path_factory: pytest.TempPathFactory) -> Path:
    """Session-scoped DRIFTBUSTER_DATA_ROOT so the module caches DLLs under tmp, not the real home."""

    return tmp_path_factory.mktemp("driftbuster-data-root")


@pytest.fixture(scope="session")
def published_backend(backend_data_root: Path) -> Path:
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

    publish_dir = Path("gui/DriftBuster.Backend/bin/Debug/published")
    manifest_path = Path("cli/DriftBuster.PowerShell/DriftBuster.psd1")
    backend_version = "0.0.2"
    manifest_text = manifest_path.read_text(encoding="utf-8")
    match = re.search(r"BackendVersion\s*=\s*'([^']+)'", manifest_text)
    if match:
        backend_version = match.group(1)

    # Mirrors DriftbusterPaths.GetCacheDirectory("powershell", "backend", <version>) under the tmp data root.
    cache_dir = backend_data_root / "cache" / "powershell" / "backend" / backend_version
    cache_dir.mkdir(parents=True, exist_ok=True)
    for dependency in publish_dir.iterdir():
        if dependency.is_file():
            shutil.copy2(dependency, cache_dir / dependency.name)

    return MODULE_PATH.resolve()


def _run_powershell(
    module_path: Path, body: str, *, check: bool = True, data_root: Path | None = None
) -> subprocess.CompletedProcess[str]:
    module_literal = _ps_literal(str(module_path))
    script = (
        f"$module = {module_literal}; Add-Type -AssemblyName System.Text.Json; "
        f"Import-Module $module -Force; {body}"
    )
    env = dict(os.environ)
    if data_root is not None:
        env["DRIFTBUSTER_DATA_ROOT"] = str(data_root)
    return subprocess.run(
        ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
        check=check,
        capture_output=True,
        text=True,
        env=env,
    )


def _capture_json(module_path: Path, body: str, *, depth: int = 6, data_root: Path | None = None) -> Any:
    command = f"{body} | ConvertTo-Json -Depth {depth}"
    completed = _run_powershell(module_path, command, data_root=data_root)
    output = completed.stdout.strip()
    if not output:
        raise AssertionError("PowerShell command produced no output")
    return json.loads(output)


def test_ping_returns_pong(published_backend: Path, backend_data_root: Path) -> None:
    payload = _capture_json(published_backend, "$result = Test-DriftBusterPing; $result", data_root=backend_data_root)
    assert payload["status"] == "pong"


def test_diff_pair_round_trip(published_backend: Path, backend_data_root: Path) -> None:
    body = """
    $left = New-TemporaryFile
    $right = New-TemporaryFile
    Set-Content -LiteralPath $left 'alpha' -NoNewline
    Set-Content -LiteralPath $right 'beta' -NoNewline
    $result = Invoke-DriftBusterDiff -Left $left -Right $right
    $result
    """
    payload = _capture_json(published_backend, body, depth=8, data_root=backend_data_root)

    assert payload["comparisons"]
    comparison = payload["comparisons"][0]
    assert comparison["plan"]["before"].startswith("alpha")
    assert comparison["plan"]["after"].startswith("beta")


def test_run_profile_creates_artifacts(published_backend: Path, backend_data_root: Path) -> None:
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
    $result = Invoke-DriftBusterRunProfile -Profile $profile -BaseDir $base -Confirm:$false
    $result
    """

    try:
        payload = _capture_json(published_backend, body, depth=6, data_root=backend_data_root)
    except subprocess.CalledProcessError as exc:  # pragma: no cover - platform guard
        stderr_clean = _strip_ansi(exc.stderr or "")
        if "Unable to find type" in stderr_clean:
            pytest.skip("System.Text.Json enum converter unavailable in current PowerShell runtime")
        raise

    assert payload["files"]
    assert any(entry["destination"].endswith("baseline.txt") for entry in payload["files"])


def test_import_surfaces_backend_missing(tmp_path: Path, backend_data_root: Path) -> None:
    module_root = tmp_path / "module"
    shutil.copytree(MODULE_PATH.parent, module_root)

    packaged_backend = module_root / "DriftBuster.Backend.dll"
    if packaged_backend.exists():
        packaged_backend.unlink()

    result = _run_powershell(module_root / MODULE_PATH.name, "", check=False, data_root=backend_data_root)

    assert result.returncode != 0
    stderr = result.stderr or ""
    stderr_clean = _strip_ansi(stderr)
    assert (
        "DriftBusterBackendMissing" in stderr_clean
        or "Unable to load DriftBuster.Backend.dll for the PowerShell module." in stderr_clean
    )
    assert "dotnet publish gui/DriftBuster.Backend/DriftBuster.Backend.csproj" in stderr_clean
