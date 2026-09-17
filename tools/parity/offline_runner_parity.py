"""Run the ``offline-run`` parity surface: ``offline_runner.execute_config_path`` against ``scripts/driftbuster-offline-runner.ps1``.

Temporary; deleted together with the Python package.

    python tools/parity/offline_runner_parity.py run --work <scratch dir> [--powershell pwsh|powershell] [--platform linux|windows]
        [--script <runner script>] <case dir>...
    python tools/parity/offline_runner_parity.py --self-test

A case is a directory holding ``config.json`` and, optionally, ``workdir/`` and ``case.json``. Each side runs in a run directory built
afresh at the same absolute path: a copy of ``workdir/`` with ``config.json`` at its root, so relative paths in the config name files of
the tree. ``case.json`` (all keys optional):

    platforms   the platforms the case runs on: ``linux``, ``windows`` (default both)
    env         {name: value} added to both sides' environment
    databases   {relative path: SQL script beside case.json}: SQLite databases built with ``executescript``
    links       list of {"path", "target", "directory"}: symbolic links created in the run directory (target text as given)
    fifos       list of relative paths: FIFOs created in the run directory (Linux)
    junctions   list of {"path", "target"}: directory junctions (Windows)
    registry    {"key": "Software\\DriftBusterOfflineRunParity\\<name>", "tree": {"values": [...], "keys": {name: tree}}}: an HKCU tree
                created before the runs and deleted after them (Windows). A value is {"name", "type", "data"}; the type is a winreg
                ``REG_*`` name or a type number. REG_SZ and REG_EXPAND_SZ data is a string, REG_MULTI_SZ a list, REG_DWORD and REG_QWORD
                an integer, every other type (REG_BINARY, REG_NONE, REG_LINK, the resource lists, type numbers) a hex string
    mtimes      {relative path: POSIX seconds}: modification times set on entries of the run directory once it is built
    dpapi_keysets  {relative path: keyset}: keysets written on Windows (once per case, the same file for both sides), where each
                key entry holding ``protect_hex`` gets the DPAPI blob of those bytes (``scope`` ``machine`` protects with the machine
                key) as ``data`` and ``encoding: dpapi``
    keyset      the keyset (relative path) that decrypts the case's encrypted packages
    home        a directory of ``workdir/`` moved beside the run directory and named by HOME and USERPROFILE on both sides, so ``~``
                resolves into it while what the PowerShell host writes under a profile stays out of the comparison

``{run}`` and ``{sep}`` are replaced in config.json (JSON-escaped), env values and link and junction targets. Both sides get the same
environment (PYTHONPATH naming the repository's ``src``, no bytecode files). The script runs from a runner directory holding only the
script and the packaged ``secret_rules.json``, the layout the runner looks beside itself for.

Compared, after the acceptors below: the result (error ``Type: message``, or the script's output object and the same fields of the
Python result) and every entry left in the run directory (sorted relative paths; a file by its bytes, a directory, a link by its target).
A zip file is compared by its entries (sorted names and bytes, listed as ``<zip>!/<entry>``); its compressed bytes are not (see
expected_divergences.md, "Offline runner script").

Acceptors, applied to both runs; each validates what it replaces and fails the case when that does not hold:

    run-clock       A compact stamp (``%Y%m%dT%H%M%SZ``: the run timestamp in the staging directory and package names and the manifest) or
                    an ISO 8601 UTC stamp (log lines, ``generated_at``, a snapshot's ``captured_at``) in a path, a file, a zip entry or the
                    result becomes its shape (digits as ``#``) only when it falls inside that side's run window.
    content-hash    A SHA-256 hex digest of a file the acceptors rewrote (a SQL snapshot's hash in the manifest, the encrypted package's
                    hash) is replaced by the digest of the rewritten bytes, wherever it appears.
    encrypted-package  An encrypted package (``dpapi-aes/v1`` envelope) is decrypted with the case's keyset; its MAC must verify and
                    ``package.size`` must be the plaintext length. ``iv``, ``ciphertext``, ``mac`` and ``size`` become ``<verified>``
                    and the plaintext zip's entries are compared as ``<package>!/<entry>``.
    host-platform   Off Windows only: the manifest's ``host.platform`` becomes ``<platform>`` (``platform.platform()`` against the
                    runtime's operating system description; reproduced on Windows, so compared there). Counted as an expected
                    divergence.

Prints ``ok   offline-run <case>`` or ``FAIL offline-run <case>: <reason>`` with the differences, then ``#counts <cases> <expected
divergences>``; exits 1 when any case fails.
"""

from __future__ import annotations

import base64
import difflib
import hashlib
import hmac
import io
import json
import os
import re
import shutil
import sqlite3
import stat
import subprocess
import sys
import time
import zipfile
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "src"))
SCRIPT = REPO / "scripts" / "driftbuster-offline-runner.ps1"
PACKAGED_RULES = REPO / "src" / "driftbuster" / "secret_rules.json"
MANIFEST_SCHEMA = "https://driftbuster.dev/offline-runner/manifest/v1"
ENCRYPTED_SCHEMA = "https://driftbuster.dev/offline-runner/encryption/dpapi-aes/v1"
REGISTRY_PREFIX = "Software\\DriftBusterOfflineRunParity\\"
COMPACT_STAMP = re.compile(rb"(?<![0-9])\d{8}T\d{6}Z(?![0-9])")
ISO_STAMP = re.compile(rb"(?<![0-9])\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?(?:\+00:00|Z)(?![0-9])")
HEX_DIGEST = re.compile(rb"(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])")
HOST_PLATFORM = re.compile(rb'("host": \{\n\s+"computer_name": "(?:[^"\\]|\\.)*",\n\s+"platform": )"(?:[^"\\]|\\.)*"')
CLOCK_SLACK_SECONDS = 2.0
TIMEOUT_SECONDS = float(os.environ.get("PARITY_PORT_TIMEOUT", "600"))
WINDOWS = os.name == "nt"

PS_DRIVER = r"""param([string]$Script, [string]$Config, [string]$Out)
$ErrorActionPreference = 'Stop'
$result = [ordered]@{ error = $null }
try {
    $output = @(& $Script -ConfigPath $Config)
    if ($output.Count -ne 1) {
        throw "NonScript: the script wrote $($output.Count) objects"
    }

    $names = @('StagingDirectory', 'PackagePath', 'EncryptedPackagePath', 'UnencryptedPackagePath', 'ManifestPath', 'LogPath')
    foreach ($name in $names + @('FilesCollected')) {
        $result[$name] = $output[0].$name
    }
}
catch {
    $result['error'] = $_.Exception.Message
}

[System.IO.File]::WriteAllText($Out, (ConvertTo-Json -InputObject $result -Depth 3), (New-Object System.Text.UTF8Encoding $false))
"""


class CaseError(Exception):
    """A case that cannot be built or whose acceptor preconditions fail."""


@dataclass
class Run:
    result: dict[str, object]
    entries: dict[str, tuple[str, bytes]]
    started: float = 0.0
    finished: float = 0.0
    applied: set[str] = field(default_factory=set)


# ----------------------------------------------------------------------------------------------------------------------------------
# Python side
# ----------------------------------------------------------------------------------------------------------------------------------


def py_side(config: str, out: str) -> int:
    from driftbuster import offline_runner

    def text(value: object) -> str | None:
        return None if value is None else str(value)

    try:
        run = offline_runner.execute_config_path(config)
        result: dict[str, object] = {"error": None}
        result["StagingDirectory"] = text(run.staging_dir)
        result["PackagePath"] = text(run.package_path)
        result["EncryptedPackagePath"] = text(run.encrypted_package_path)
        result["UnencryptedPackagePath"] = text(run.unencrypted_package_path)
        result["ManifestPath"] = text(run.manifest_path)
        result["LogPath"] = text(run.log_path)
        result["FilesCollected"] = len(run.files)
    except Exception as exc:
        result = {"error": f"{type(exc).__name__}: {exc}"}
    Path(out).write_text(json.dumps(result), encoding="utf-8")
    return 0


# ----------------------------------------------------------------------------------------------------------------------------------
# Building a run directory
# ----------------------------------------------------------------------------------------------------------------------------------


def substitute(text: str, run_dir: Path, *, json_escaped: bool) -> str:
    def escape(value: str) -> str:
        return json.dumps(value)[1:-1] if json_escaped else value

    return text.replace("{run}", escape(str(run_dir))).replace("{sep}", escape(os.sep))


def dpapi_protect(data: bytes, *, machine: bool) -> bytes:
    import ctypes
    from ctypes import wintypes

    class Blob(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_ubyte))]

    buffer = ctypes.create_string_buffer(data, len(data))
    blob_in = Blob(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
    blob_out = Blob()
    flags = 0x1 | (0x4 if machine else 0)  # CRYPTPROTECT_UI_FORBIDDEN, CRYPTPROTECT_LOCAL_MACHINE
    if not ctypes.windll.crypt32.CryptProtectData(ctypes.byref(blob_in), None, None, None, None, flags, ctypes.byref(blob_out)):
        raise CaseError(f"CryptProtectData failed: {ctypes.GetLastError()}")
    try:
        return ctypes.string_at(blob_out.pbData, blob_out.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(blob_out.pbData)


def home_dir(run_dir: Path) -> Path:
    return run_dir.parent / f"{run_dir.name}.home"


def build_run_dir(case_dir: Path, spec: dict, run_dir: Path, generated: dict[str, str]) -> None:
    """Builds the run directory; DPAPI keysets are protected once per case (``generated``) so both sides read the same file."""
    workdir = case_dir / "workdir"
    if workdir.is_dir():
        shutil.copytree(workdir, run_dir, symlinks=True)
    else:
        run_dir.mkdir(parents=True)
    config_text = (case_dir / "config.json").read_text(encoding="utf-8")
    (run_dir / "config.json").write_text(substitute(config_text, run_dir, json_escaped=True), encoding="utf-8", newline="")
    if spec.get("home"):
        home = home_dir(run_dir)
        remove_tree(home)
        os.replace(run_dir / spec["home"], home)
    for relative, script in (spec.get("databases") or {}).items():
        target = run_dir / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        connection = sqlite3.connect(target)
        try:
            connection.executescript((case_dir / script).read_text(encoding="utf-8"))
            connection.commit()
        finally:
            connection.close()
    for link in spec.get("links") or ():
        path = run_dir / link["path"]
        path.parent.mkdir(parents=True, exist_ok=True)
        os.symlink(substitute(link["target"], run_dir, json_escaped=False), path, target_is_directory=bool(link.get("directory")))
    for relative in spec.get("fifos") or ():
        os.mkfifo(run_dir / relative)
    for junction in spec.get("junctions") or ():
        path = run_dir / junction["path"]
        target = substitute(junction["target"], run_dir, json_escaped=False)
        subprocess.run(["cmd", "/c", "mklink", "/J", str(path), target], check=True, stdout=subprocess.DEVNULL)
    for relative, keyset in (spec.get("dpapi_keysets") or {}).items():
        if relative not in generated:
            payload = json.loads(json.dumps(keyset))
            for entry in payload.values():
                if isinstance(entry, dict) and "protect_hex" in entry:
                    machine = str(entry.get("scope", "")).lower() in {"machine", "local_machine", "machinekey", "local-machine"}
                    blob = dpapi_protect(bytes.fromhex(entry.pop("protect_hex")), machine=machine)
                    entry["data"] = base64.b64encode(blob).decode("ascii")
                    entry["encoding"] = "dpapi"
            generated[relative] = json.dumps(payload, indent=2)
        target = run_dir / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(generated[relative], encoding="utf-8")
    for relative, seconds in (spec.get("mtimes") or {}).items():
        os.utime(run_dir / relative, (seconds, seconds))


REGISTRY_TYPES = (
    "REG_SZ",
    "REG_EXPAND_SZ",
    "REG_MULTI_SZ",
    "REG_DWORD",
    "REG_QWORD",
    "REG_BINARY",
    "REG_NONE",
    "REG_DWORD_BIG_ENDIAN",
    "REG_LINK",
    "REG_RESOURCE_LIST",
    "REG_FULL_RESOURCE_DESCRIPTOR",
    "REG_RESOURCE_REQUIREMENTS_LIST",
)
REGISTRY_TEXT_TYPES = ("REG_SZ", "REG_EXPAND_SZ", "REG_MULTI_SZ", "REG_DWORD", "REG_QWORD")


def registry_create(spec: dict) -> None:
    import winreg

    key_path = spec["key"]
    if not key_path.startswith(REGISTRY_PREFIX):
        raise CaseError(f"registry key must lie under HKCU\\{REGISTRY_PREFIX}")

    def fill(handle: object, tree: dict) -> None:
        for value in tree.get("values") or ():
            kind = value["type"]
            if kind not in REGISTRY_TYPES and not (isinstance(kind, int) and not isinstance(kind, bool) and 0 <= kind <= 0xFFFFFFFF):
                raise CaseError(f"unknown registry type {kind}")
            data = value["data"]
            if kind not in REGISTRY_TEXT_TYPES:
                data = bytes.fromhex(data)
            winreg.SetValueEx(handle, value["name"], 0, kind if isinstance(kind, int) else getattr(winreg, kind), data)
        for name, child in (tree.get("keys") or {}).items():
            with winreg.CreateKeyEx(handle, name, 0, winreg.KEY_ALL_ACCESS) as child_handle:
                fill(child_handle, child)

    registry_delete(spec)
    with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, key_path, 0, winreg.KEY_ALL_ACCESS) as handle:
        fill(handle, spec.get("tree") or {})


def registry_delete(spec: dict) -> None:
    import winreg

    def delete(parent: object, path: str) -> None:
        try:
            with winreg.OpenKey(parent, path, 0, winreg.KEY_ALL_ACCESS) as handle:
                while True:
                    try:
                        delete(handle, winreg.EnumKey(handle, 0))
                    except OSError:
                        break
            winreg.DeleteKey(parent, path)
        except FileNotFoundError:
            pass

    if spec["key"].startswith(REGISTRY_PREFIX):
        delete(winreg.HKEY_CURRENT_USER, spec["key"])


def remove_tree(path: Path) -> None:
    """Deletes a run directory without following links or junctions."""

    if not os.path.lexists(path):
        return
    for entry in os.scandir(path):
        full = Path(entry.path)
        if entry.is_symlink() or (WINDOWS and os.path.isjunction(full)):
            if WINDOWS and os.lstat(full).st_file_attributes & stat.FILE_ATTRIBUTE_DIRECTORY:
                os.rmdir(full)
            else:
                os.unlink(full)
        elif entry.is_dir(follow_symlinks=False):
            remove_tree(full)
        else:
            os.chmod(full, stat.S_IWRITE | stat.S_IREAD)
            os.unlink(full)
    os.rmdir(path)


# ----------------------------------------------------------------------------------------------------------------------------------
# Running and snapshotting
# ----------------------------------------------------------------------------------------------------------------------------------


def snapshot(run_dir: Path) -> dict[str, tuple[str, bytes]]:
    entries: dict[str, tuple[str, bytes]] = {}

    def walk(directory: Path) -> None:
        for entry in sorted(os.scandir(directory), key=lambda item: item.name):
            full = Path(entry.path)
            relative = full.relative_to(run_dir).as_posix()
            if WINDOWS and os.path.isjunction(full):
                entries[relative] = ("junction", os.readlink(full).encode("utf-8"))
            elif entry.is_symlink():
                entries[relative] = ("link", os.readlink(full).encode("utf-8", "surrogateescape"))
            elif entry.is_dir(follow_symlinks=False):
                entries[relative] = ("dir", b"")
                walk(full)
            elif entry.is_file(follow_symlinks=False):
                data = full.read_bytes()
                if relative.lower().endswith(".zip") and zipfile.is_zipfile(io.BytesIO(data)):
                    entries[relative] = ("zip", b"")
                    add_zip_entries(entries, relative, data)
                else:
                    entries[relative] = ("file", data)
            else:
                entries[relative] = ("special", b"")

    walk(run_dir)
    return entries


def add_zip_entries(entries: dict[str, tuple[str, bytes]], prefix: str, data: bytes) -> None:
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        for info in archive.infolist():
            entries[f"{prefix}!/{info.filename}"] = ("zip-entry", archive.read(info))


def run_side(side: str, case_dir: Path, spec: dict, run_dir: Path, args, runner: Path, driver: Path, env: dict[str, str],
             generated: dict[str, str]) -> Run:
    remove_tree(run_dir)
    build_run_dir(case_dir, spec, run_dir, generated)
    out = run_dir.parent / f"{run_dir.name}.{side}.result.json"
    config = str(run_dir / "config.json")
    if side == "py":
        command = [sys.executable, str(Path(__file__).resolve()), "py-side", config, str(out)]
    else:
        command = [args.powershell, "-NoProfile", "-NonInteractive"]
        if WINDOWS:
            command += ["-ExecutionPolicy", "Bypass"]
        command += ["-File", str(driver), "-Script", str(runner), "-Config", config, "-Out", str(out)]
    started = time.time()
    completed = subprocess.run(command, cwd=run_dir, env=env, capture_output=True, timeout=TIMEOUT_SECONDS, check=False)
    finished = time.time()
    if completed.returncode != 0 or not out.is_file():
        raise CaseError(
            f"{side} driver exited {completed.returncode}: {completed.stderr.decode('utf-8', 'replace')[-2000:]}"
            f"{completed.stdout.decode('utf-8', 'replace')[-2000:]}"
        )
    result = json.loads(out.read_text(encoding="utf-8-sig"))
    out.unlink()
    entries = snapshot(run_dir)
    remove_tree(run_dir)
    remove_tree(home_dir(run_dir))
    return Run(result=result, entries=entries, started=started, finished=finished)


# ----------------------------------------------------------------------------------------------------------------------------------
# Acceptors
# ----------------------------------------------------------------------------------------------------------------------------------


def _shape(match: re.Match[bytes]) -> bytes:
    return re.sub(rb"\d", b"#", match.group(0))


def _stamp_seconds(raw: bytes) -> float:
    text = raw.decode("ascii")
    if COMPACT_STAMP.fullmatch(raw):
        return datetime.strptime(text, "%Y%m%dT%H%M%SZ").replace(tzinfo=UTC).timestamp()
    return datetime.fromisoformat(text.replace("Z", "+00:00")).timestamp()


def _clock(data: bytes, run: Run) -> bytes:
    low, high = int(run.started) - CLOCK_SLACK_SECONDS, run.finished + CLOCK_SLACK_SECONDS

    def replace(match: re.Match[bytes]) -> bytes:
        try:
            seconds = _stamp_seconds(match.group(0))
        except ValueError:
            return match.group(0)
        return _shape(match) if low <= seconds <= high else match.group(0)

    return ISO_STAMP.sub(replace, COMPACT_STAMP.sub(replace, data))


def accept_run_clock(run: Run) -> dict[bytes, bytes]:
    """Rewrites clock readings in paths, contents and the result; returns raw digest -> rewritten digest for every rewritten file."""

    digests: dict[bytes, bytes] = {}
    rewritten: dict[str, tuple[str, bytes]] = {}
    for key, (kind, data) in run.entries.items():
        new_key = _clock(key.encode("utf-8", "surrogateescape"), run).decode("utf-8", "surrogateescape")
        new_data = _clock(data, run) if kind in {"file", "zip-entry", "link"} else data
        if new_key != key or new_data != data:
            run.applied.add("run-clock")
        if new_data != data:
            digests[hashlib.sha256(data).hexdigest().encode()] = hashlib.sha256(new_data).hexdigest().encode()
        rewritten[new_key] = (kind, new_data)
    run.entries = rewritten
    for name, value in list(run.result.items()):
        if isinstance(value, str):
            new_value = _clock(value.encode("utf-8"), run).decode("utf-8")
            if new_value != value:
                run.applied.add("run-clock")
                run.result[name] = new_value
    return digests


def accept_encrypted_package(run: Run, keys: tuple[bytes, bytes] | None) -> dict[bytes, bytes]:
    from cryptography.hazmat.primitives import padding
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

    digests: dict[bytes, bytes] = {}
    for key, (kind, data) in list(run.entries.items()):
        if kind != "file" or ENCRYPTED_SCHEMA.encode() not in data:
            continue
        try:
            envelope = json.loads(data)
        except ValueError:
            continue
        if not isinstance(envelope, dict) or envelope.get("schema") != ENCRYPTED_SCHEMA:
            continue
        if keys is None:
            raise CaseError(f"{key}: an encrypted package but the case declares no keyset")
        aes_key, mac_key = keys
        iv, ciphertext, mac = (base64.b64decode(envelope[name]) for name in ("iv", "ciphertext", "mac"))
        if not hmac.compare_digest(hmac.new(mac_key, iv + ciphertext, hashlib.sha256).digest(), mac):
            raise CaseError(f"{key}: the MAC does not verify with the case's keyset")
        decryptor = Cipher(algorithms.AES(aes_key), modes.CBC(iv)).decryptor()
        unpadder = padding.PKCS7(128).unpadder()
        plaintext = unpadder.update(decryptor.update(ciphertext) + decryptor.finalize()) + unpadder.finalize()
        if envelope.get("package", {}).get("size") != len(plaintext):
            raise CaseError(f"{key}: package.size {envelope.get('package', {}).get('size')} is not the plaintext length {len(plaintext)}")
        new_data = data
        for name in ("iv", "ciphertext", "mac"):
            needle = f'"{name}": "{envelope[name]}"'.encode()
            if new_data.count(needle) != 1:
                raise CaseError(f"{key}: {name} is not written once as a plain JSON string")
            new_data = new_data.replace(needle, f'"{name}": "<verified>"'.encode())
        new_data, count = re.subn(rb'("size": )' + str(len(plaintext)).encode() + rb"(?![0-9])", rb'\1"<verified>"', new_data)
        if count != 1:
            raise CaseError(f"{key}: package.size is not written once")
        digests[hashlib.sha256(data).hexdigest().encode()] = hashlib.sha256(new_data).hexdigest().encode()
        run.entries[key] = ("file", new_data)
        if zipfile.is_zipfile(io.BytesIO(plaintext)):
            add_zip_entries(run.entries, key, plaintext)
        else:
            run.entries[f"{key}!plaintext"] = ("file", plaintext)
        run.applied.add("encrypted-package")
    return digests


def accept_content_hash(run: Run, digests: dict[bytes, bytes]) -> None:
    if not digests:
        return

    def replace(match: re.Match[bytes]) -> bytes:
        return digests.get(match.group(0), match.group(0))

    for key, (kind, data) in list(run.entries.items()):
        if kind in {"file", "zip-entry"}:
            new_data = HEX_DIGEST.sub(replace, data)
            if new_data != data:
                run.entries[key] = (kind, new_data)
                run.applied.add("content-hash")


def accept_host_platform(run: Run) -> None:
    for key, (kind, data) in list(run.entries.items()):
        if kind in {"file", "zip-entry"} and MANIFEST_SCHEMA.encode() in data:
            new_data, count = HOST_PLATFORM.subn(rb'\1"<platform>"', data)
            if count:
                run.entries[key] = (kind, new_data)
                run.applied.add("host-platform")


def normalise(run: Run, keys: tuple[bytes, bytes] | None, *, platform: str) -> None:
    envelope_digests = accept_encrypted_package(run, keys)
    clock_digests = accept_run_clock(run)
    # An envelope the clock acceptor rewrote again (its original_name holds the run stamp) maps to its final digest.
    digests = {raw: clock_digests.get(middle, middle) for raw, middle in envelope_digests.items()}
    digests.update({raw: final for raw, final in clock_digests.items() if raw not in digests})
    accept_content_hash(run, digests)
    if platform != "windows":
        accept_host_platform(run)


# ----------------------------------------------------------------------------------------------------------------------------------
# Comparison
# ----------------------------------------------------------------------------------------------------------------------------------


def describe(kind: str, data: bytes) -> list[str]:
    if kind in {"dir", "zip", "special"}:
        return [kind]
    if kind in {"link", "junction"}:
        return [f"{kind} -> {data.decode('utf-8', 'replace')}"]
    try:
        return [f"{kind}:", *data.decode("utf-8").splitlines(keepends=True)]
    except UnicodeDecodeError:
        return [f"{kind} (hex):"] + [data[offset : offset + 32].hex() + "\n" for offset in range(0, len(data), 32)]


def differences(py: Run, ps: Run) -> list[str]:
    lines: list[str] = []
    if py.result != ps.result:
        lines.append(f"  result\n    py {json.dumps(py.result, sort_keys=True)}\n    ps {json.dumps(ps.result, sort_keys=True)}")
    for key in sorted(set(py.entries) | set(ps.entries)):
        left, right = py.entries.get(key), ps.entries.get(key)
        if left == right:
            continue
        if left is None or right is None:
            lines.append(f"  {key}: only {'ps' if left is None else 'py'}")
            continue
        diff = difflib.unified_diff(describe(*left), describe(*right), "py", "ps", n=1)
        lines.append(f"  {key}\n" + "".join("    " + line if line.endswith("\n") else "    " + line + "\n" for line in list(diff)[:80]))
    return lines


def load_spec(case_dir: Path) -> dict:
    path = case_dir / "case.json"
    return json.loads(path.read_text(encoding="utf-8")) if path.is_file() else {}


def run_case(case_dir: Path, args, runner: Path, driver: Path) -> tuple[bool, bool]:
    spec = load_spec(case_dir)
    run_dir = Path(args.work).resolve() / "run" / case_dir.name
    run_dir.parent.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, PYTHONPATH=str(REPO / "src"), PYTHONDONTWRITEBYTECODE="1")
    if spec.get("home"):
        env["HOME"] = env["USERPROFILE"] = str(home_dir(run_dir))
    for name, value in (spec.get("env") or {}).items():
        env[name] = substitute(value, run_dir, json_escaped=False)
    registry = spec.get("registry")
    generated: dict[str, str] = {}
    try:
        if registry:
            registry_create(registry)
        keys = None
        if spec.get("keyset"):
            from driftbuster import offline_runner

            build_run_dir(case_dir, spec, run_dir, generated)
            try:
                keys = offline_runner._load_encryption_keyset(run_dir / spec["keyset"])
            finally:
                remove_tree(run_dir)
                remove_tree(home_dir(run_dir))
        py = run_side("py", case_dir, spec, run_dir, args, runner, driver, env, generated)
        ps = run_side("ps", case_dir, spec, run_dir, args, runner, driver, env, generated)
        for run in (py, ps):
            normalise(run, keys, platform=args.platform)
    finally:
        if registry:
            registry_delete(registry)
        remove_tree(run_dir)
        remove_tree(home_dir(run_dir))
    found = differences(py, ps)
    if found:
        print(f"FAIL offline-run {case_dir.name}: outputs differ")
        print("".join(line if line.endswith("\n") else line + "\n" for line in found), end="")
        return False, False
    expected = "host-platform" in py.applied or "host-platform" in ps.applied
    if ("host-platform" in py.applied) != ("host-platform" in ps.applied):
        print(f"FAIL offline-run {case_dir.name}: host-platform applied to one side only")
        return False, False
    outcome = f"error {py.result['error']}" if py.result.get("error") else f"{py.result.get('FilesCollected')} files"
    notes = ", ".join([outcome, *sorted(py.applied | ps.applied)])
    print(f"ok   offline-run {case_dir.name} ({notes})")
    return True, expected


def run(args) -> int:
    work = Path(args.work).resolve()
    work.mkdir(parents=True, exist_ok=True)
    runner_dir = work / "runner"
    remove_tree(runner_dir)
    runner_dir.mkdir()
    runner = runner_dir / SCRIPT.name
    shutil.copyfile(args.script, runner)
    shutil.copyfile(PACKAGED_RULES, runner_dir / PACKAGED_RULES.name)
    driver = work / "driver.ps1"
    driver.write_text(PS_DRIVER, encoding="utf-8")
    cases = expected = failures = 0
    for case in args.cases:
        case_dir = Path(case).resolve()
        platforms = load_spec(case_dir).get("platforms") or ["linux", "windows"]
        if args.platform not in platforms:
            continue
        cases += 1
        try:
            ok, was_expected = run_case(case_dir, args, runner, driver)
        except Exception as exc:
            ok, was_expected = False, False
            print(f"FAIL offline-run {case_dir.name}: {type(exc).__name__}: {exc}")
        failures += 0 if ok else 1
        expected += 1 if was_expected else 0
    remove_tree(runner_dir)
    driver.unlink()
    print(f"#counts {cases} {expected}")
    return 1 if failures or not cases else 0


# ----------------------------------------------------------------------------------------------------------------------------------
# Self-test
# ----------------------------------------------------------------------------------------------------------------------------------


def self_test() -> int:
    from cryptography.hazmat.primitives import padding
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

    failures: list[str] = []

    def check(name: str, condition: bool) -> None:
        if not condition:
            failures.append(name)

    started = datetime(2026, 1, 2, 3, 4, 5, tzinfo=UTC).timestamp()
    inside_compact, inside_iso = b"20260102T030406Z", b"2026-01-02T03:04:06.123456+00:00"
    outside_compact, outside_iso = b"20250102T030406Z", b"2026-01-02T03:14:06Z"

    def make(entries: dict[str, tuple[str, bytes]], result: dict | None = None) -> Run:
        return Run(result=result or {"error": None}, entries=dict(entries), started=started + 0.5, finished=started + 1.0)

    run = make(
        {
            "out/p-20260102T030406Z.zip": ("zip", b""),
            "log": ("file", b"[2026-01-02T03:04:06Z] started\n[" + outside_iso + b"] late\n"),
            "snap": ("file", b'{"captured_at": "' + inside_iso + b'"}'),
            "old": ("file", outside_compact + b" 120260102T030406Z " + inside_compact + b"9"),
        },
        {"error": None, "PackagePath": "/x/p-20260102T030406Z.zip"},
    )
    raw_snap = hashlib.sha256(run.entries["snap"][1]).hexdigest().encode()
    digests = accept_run_clock(run)
    check("clock: compact stamp in a path", "out/p-########T######Z.zip" in run.entries)
    check("clock: log stamp inside the window", run.entries["log"][1].startswith(b"[####-##-##T##:##:##Z] started"))
    check("clock: ISO stamp outside the window is kept", outside_iso in run.entries["log"][1])
    check("clock: fraction and offset keep their shape", run.entries["snap"][1] == b'{"captured_at": "####-##-##T##:##:##.######+##:##"}')
    check("clock: stamps outside the window or inside digits are kept", b"#" not in run.entries["old"][1])
    check("clock: result paths", run.result["PackagePath"] == "/x/p-########T######Z.zip")
    check("clock: digest map covers rewritten files", raw_snap in digests and b"old" not in digests)
    check("clock: records itself", "run-clock" in run.applied)

    untouched = make({"a": ("file", b"no clock here " + outside_compact)})
    check("clock: nothing to rewrite", accept_run_clock(untouched) == {} and "run-clock" not in untouched.applied)

    hashed = make({"manifest": ("file", b'"sha256": "' + raw_snap + b'", "other": "' + b"a" * 64 + b'", "long": "' + raw_snap + b'0"')})
    accept_content_hash(hashed, {raw_snap: b"f" * 64})
    check("hash: a recorded digest is replaced", b'"sha256": "' + b"f" * 64 + b'"' in hashed.entries["manifest"][1])
    hashed_text = hashed.entries["manifest"][1]
    check("hash: other digests and longer hex runs are kept", b"a" * 64 in hashed_text and raw_snap + b"0" in hashed_text)

    aes_key, mac_key = b"A" * 32, b"B" * 32
    archive = io.BytesIO()
    with zipfile.ZipFile(archive, "w") as handle:
        handle.writestr("manifest.json", "{}")
    plaintext = archive.getvalue()
    iv = b"\x01" * 16
    padder = padding.PKCS7(128).padder()
    encryptor = Cipher(algorithms.AES(aes_key), modes.CBC(iv)).encryptor()
    ciphertext = encryptor.update(padder.update(plaintext) + padder.finalize()) + encryptor.finalize()
    mac = hmac.new(mac_key, iv + ciphertext, hashlib.sha256).digest()

    def envelope(size: int, tag: bytes = mac) -> bytes:
        payload = {
            "schema": ENCRYPTED_SCHEMA,
            "algorithm": "aes-256-cbc+hmac-sha256",
            "iv": base64.b64encode(iv).decode(),
            "ciphertext": base64.b64encode(ciphertext).decode(),
            "mac": base64.b64encode(tag).decode(),
            "package": {"original_name": "p.zip", "size": size},
        }
        return json.dumps(payload, indent=2).encode()

    sealed = make({"p.zip.enc": ("file", envelope(len(plaintext)))})
    enc_digests = accept_encrypted_package(sealed, (aes_key, mac_key))
    check("encrypted: entries of the plaintext zip", sealed.entries.get("p.zip.enc!/manifest.json") == ("zip-entry", b"{}"))
    check("encrypted: verified fields", sealed.entries["p.zip.enc"][1].count(b"<verified>") == 4 and len(enc_digests) == 1)
    for label, bad in (("wrong MAC", envelope(len(plaintext), b"\x00" * 32)), ("wrong size", envelope(len(plaintext) + 1))):
        try:
            accept_encrypted_package(make({"p.zip.enc": ("file", bad)}), (aes_key, mac_key))
            failures.append(f"encrypted: {label} accepted")
        except CaseError:
            pass
    try:
        accept_encrypted_package(make({"p.zip.enc": ("file", envelope(len(plaintext)))}), None)
        failures.append("encrypted: accepted without a keyset")
    except CaseError:
        pass

    manifest = json.dumps(
        {"host": {"computer_name": "h", "platform": "Linux-x", "user": "u"}, "metadata": {"platform": "keep"}, "schema": MANIFEST_SCHEMA},
        indent=2,
        sort_keys=True,
    ).encode()
    platformed = make({"m": ("file", manifest), "other": ("file", b'"platform": "Linux-x"')})
    accept_host_platform(platformed)
    platformed_text = platformed.entries["m"][1]
    check("platform: host.platform only", b'"platform": "<platform>"' in platformed_text and b'"platform": "keep"' in platformed_text)
    check("platform: not outside a manifest", platformed.entries["other"][1] == b'"platform": "Linux-x"')

    left = make({"a": ("file", b"x\n")}, {"error": "ValueError: a"})
    right = make({"a": ("file", b"y\n"), "b": ("dir", b"")}, {"error": "ValueError: b"})
    found = differences(left, right)
    check("differences: result, changed file and extra entry", len(found) == 3)
    check("differences: equal runs", differences(left, make({"a": ("file", b"x\n")}, {"error": "ValueError: a"})) == [])

    for failure in failures:
        print(f"self-test FAIL: {failure}")
    if not failures:
        print("self-test ok")
    return 1 if failures else 0


def main(argv: list[str]) -> int:
    if argv[:1] == ["--self-test"]:
        return self_test()
    if argv[:1] == ["py-side"] and len(argv) == 3:
        return py_side(argv[1], argv[2])
    import argparse

    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    runner = sub.add_parser("run")
    runner.add_argument("--work", required=True, help="scratch directory for the run directories")
    runner.add_argument("--powershell", default="powershell" if WINDOWS else "pwsh")
    runner.add_argument("--script", default=str(SCRIPT), help="the runner script (default: the repository's)")
    runner.add_argument("--platform", choices=("linux", "windows"), default="windows" if WINDOWS else "linux")
    runner.add_argument("cases", nargs="+")
    return run(parser.parse_args(argv))


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
