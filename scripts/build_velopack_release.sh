#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/build_velopack_release.sh --version <semver> [options]

Options:
  -v, --version <value>     Semantic version for this release (defaults to versions.yml entry).
  -r, --rid <value>         Runtime identifier to publish (default: win-x64).
  -c, --channel <name>      Optional update channel label written into the feed.
      --pack-id <value>     Override the Velopack pack id (default: com.driftbuster.gui).
  -n, --release-notes <path>
                            Markdown release notes matching docs/release-notes.md.
  -h, --help                Show this message.

The script publishes the Avalonia GUI self-contained for the selected RID
and invokes `vpk pack` to generate Velopack artifacts under `artifacts/`.
EOF
}

PACK_ID="com.driftbuster.gui"
RID="win-x64"
VERSION=""
CHANNEL=""
RELEASE_NOTES=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    -v|--version)
      VERSION="${2:-}"
      shift 2
      ;;
    -r|--rid)
      RID="${2:-}"
      shift 2
      ;;
    -c|--channel)
      CHANNEL="${2:-}"
      shift 2
      ;;
    --pack-id)
      PACK_ID="${2:-}"
      shift 2
      ;;
    -n|--release-notes)
      RELEASE_NOTES="${2:-}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSIONS_FILE="$ROOT_DIR/versions.yml"

if command -v python3 >/dev/null 2>&1; then
  PYTHON_BIN=python3
elif command -v python >/dev/null 2>&1; then
  PYTHON_BIN=python
else
  echo "Error: python3/python is required." >&2
  exit 1
fi

if [[ -z "$VERSION" ]]; then
  if [[ ! -f "$VERSIONS_FILE" ]]; then
    echo "Error: versions file '$VERSIONS_FILE' not found." >&2
    exit 1
  fi
  VERSION="$($PYTHON_BIN - "$VERSIONS_FILE" gui <<'PY'
import sys

path, key = sys.argv[1:3]
value = None
with open(path, encoding='utf-8') as fh:
    for line in fh:
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        if ':' not in line:
            continue
        k, v = line.split(':', 1)
        if k.strip() == key:
            value = v.strip()
            if value and value[0] in {'"', "'"} and value[-1] == value[0]:
                value = value[1:-1]
            break

if not value:
    print(f"versions.yml missing entry for {key}.", file=sys.stderr)
    sys.exit(1)

print(value)
PY')"
fi

if [[ -z "$VERSION" ]]; then
  echo "Error: --version is required." >&2
  usage >&2
  exit 1
fi

if [[ -z "$RELEASE_NOTES" ]]; then
  echo "Error: --release-notes is required." >&2
  usage >&2
  exit 1
fi

if [[ ! -f "$RELEASE_NOTES" ]]; then
  echo "Error: release notes file '$RELEASE_NOTES' not found." >&2
  exit 1
fi

case "$VERSION" in
  *[!0-9A-Za-z.+-]*|"" )
    echo "Error: version must be a valid semantic version." >&2
    exit 1
    ;;
esac

case "$RID" in
  win-*)
    DIRECTIVE="[win]"
    ICON_PATH="$ROOT_DIR/gui/DriftBuster.Gui/Assets/app.ico"
    ENTRY_EXE="DriftBuster.Gui.exe"
    ;;
  linux-*)
    DIRECTIVE="[linux]"
    ICON_PATH="$ROOT_DIR/gui/DriftBuster.Gui/Assets/app.png"
    ENTRY_EXE="DriftBuster.Gui"
    ;;
  osx-*|macos-*)
    DIRECTIVE="[osx]"
    ICON_PATH="$ROOT_DIR/gui/DriftBuster.Gui/Assets/app.icns"
    ENTRY_EXE="DriftBuster.Gui"
    ;;
  *)
    echo "Error: unsupported runtime identifier '$RID'." >&2
    exit 1
    ;;
esac

ABS_NOTES="$(cd "$(dirname "$RELEASE_NOTES")" && pwd)/$(basename "$RELEASE_NOTES")"

if ! grep -Eq '^# ' "$ABS_NOTES"; then
  echo "Error: release notes must begin with an H1 heading." >&2
  exit 1
fi

for SECTION in '## Core' '## Formats' '## GUI' '## Installer' '## Tooling'; do
  if ! grep -Eq "^${SECTION}" "$ABS_NOTES"; then
    echo "Error: release notes missing required section: ${SECTION}" >&2
    exit 1
  fi
done

"$PYTHON_BIN" - "$ABS_NOTES" "$VERSION" <<'PY'
import os
import re
import sys

notes_path, version = sys.argv[1:3]

with open(notes_path, encoding='utf-8') as fh:
    lines = fh.read().splitlines()

if not lines:
    print('Release notes file is empty.', file=sys.stderr)
    sys.exit(1)

match = re.match(r"#\s*DriftBuster\s+(.+)$", lines[0].strip())
if not match:
    print("Release notes must start with '# DriftBuster <version>'.", file=sys.stderr)
    sys.exit(1)

header_version = match.group(1).strip()
if header_version != version:
    print(f"Release notes header version '{header_version}' does not match --version '{version}'.", file=sys.stderr)
    sys.exit(1)


def section_block(name: str):
    target = f"## {name}"
    start = None
    for idx, line in enumerate(lines):
        if line.strip() == target:
            start = idx + 1
            break
    if start is None:
        print(f"Release notes missing section: {target}", file=sys.stderr)
        sys.exit(1)
    end = len(lines)
    for idx in range(start, len(lines)):
        if lines[idx].strip().startswith("## "):
            end = idx
            break
    entries = [ln.strip() for ln in lines[start:end] if ln.strip()]
    return entries


sections = {name: section_block(name) for name in ["Core", "GUI", "Installer", "Formats", "Tooling"]}


def has_changes(items):
    return any(not re.match(r"-\s*None\.?", item) for item in items)


def ensure_changelog(path: str):
    if not os.path.exists(path):
        print(f"Missing changelog file: {path}", file=sys.stderr)
        sys.exit(1)
    with open(path, encoding='utf-8') as fh:
        content = fh.read()
    if f"## {version}" not in content:
        print(f"Changelog {path} missing entry for {version}.", file=sys.stderr)
        sys.exit(1)


if has_changes(sections["Core"]):
    ensure_changelog(os.path.join('notes', 'changelog', 'core.md'))

if has_changes(sections["GUI"]):
    ensure_changelog(os.path.join('notes', 'changelog', 'gui.md'))

if has_changes(sections["Installer"]):
    ensure_changelog(os.path.join('notes', 'changelog', 'installer.md'))

if has_changes(sections["Tooling"]):
    ensure_changelog(os.path.join('notes', 'changelog', 'tooling.md'))

format_entries = sections["Formats"]

if has_changes(format_entries):
    for entry in format_entries:
        if re.match(r"-\s*None\.?", entry):
            continue
        m = re.match(r"-\s*([A-Za-z0-9 _.-]+):", entry)
        if not m:
            print(f"Format bullet must look like '- Name: details'. Offending entry: {entry}", file=sys.stderr)
            sys.exit(1)
        slug = m.group(1).strip().lower().replace(' ', '-')
        path = os.path.join('notes', 'changelog', 'formats', f"{slug}.md")
        ensure_changelog(path)
PY

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$ROOT/gui/DriftBuster.Gui/DriftBuster.Gui.csproj"

ARTIFACT_ROOT="$ROOT/artifacts/velopack"
PUBLISH_DIR="$ARTIFACT_ROOT/publish/$RID"
RELEASE_DIR="$ARTIFACT_ROOT/releases/$RID"

rm -rf "$PUBLISH_DIR"
rm -rf "$RELEASE_DIR"
mkdir -p "$PUBLISH_DIR"
mkdir -p "$RELEASE_DIR"

echo "Restoring local dotnet tools (ensures vpk is available)..."
dotnet tool restore >/dev/null

echo "Publishing DriftBuster GUI (${RID})..."
dotnet publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -o "$PUBLISH_DIR"

VPK_CMD=(dotnet tool run vpk)
if [[ -n "$DIRECTIVE" ]]; then
  VPK_CMD+=("$DIRECTIVE")
fi

VPK_ARGS=(pack -u "$PACK_ID" -v "$VERSION" -p "$PUBLISH_DIR" -o "$RELEASE_DIR" --releaseNotes "$ABS_NOTES")

if [[ -f "$ICON_PATH" ]]; then
  VPK_ARGS+=(-i "$ICON_PATH")
fi

if [[ -n "$ENTRY_EXE" ]]; then
  VPK_ARGS+=(-e "$ENTRY_EXE")
fi
if [[ -n "$CHANNEL" ]]; then
  VPK_ARGS+=(--channel "$CHANNEL")
fi

echo "Packaging installer with Velopack..."
"${VPK_CMD[@]}" "${VPK_ARGS[@]}"

echo "Artifacts written to: $RELEASE_DIR"
