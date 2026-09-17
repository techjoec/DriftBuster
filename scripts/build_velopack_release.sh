#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/build_velopack_release.sh --version <semver> [options]

Options:
  -v, --version <value>     Semantic version for this release (defaults to versions.json entry).
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
VERSIONS_FILE="$ROOT_DIR/versions.json"

if ! command -v jq >/dev/null 2>&1; then
  echo "Error: jq is required." >&2
  exit 1
fi

if [[ -z "$VERSION" ]]; then
  if [[ ! -f "$VERSIONS_FILE" ]]; then
    echo "Error: versions file '$VERSIONS_FILE' not found." >&2
    exit 1
  fi
  if ! VERSION="$(jq -er '.gui // empty' "$VERSIONS_FILE")"; then
    echo "versions.json missing entry for gui." >&2
    exit 1
  fi
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

header_line="$(head -n 1 "$ABS_NOTES")"
if [[ -z "$header_line" && ! -s "$ABS_NOTES" ]]; then
  echo "Release notes file is empty." >&2
  exit 1
fi
header_re='^[[:space:]]*#[[:space:]]*DriftBuster[[:space:]]+(.+)$'
if [[ ! "$header_line" =~ $header_re ]]; then
  echo "Release notes must start with '# DriftBuster <version>'." >&2
  exit 1
fi
header_version="$(sed -e 's/[[:space:]]*$//' <<<"${BASH_REMATCH[1]}")"
if [[ "$header_version" != "$VERSION" ]]; then
  echo "Release notes header version '$header_version' does not match --version '$VERSION'." >&2
  exit 1
fi

# Prints the non-blank, trimmed lines between "## <name>" and the next "## " heading.
section_entries() {
  awk -v target="## $1" '
    { line = $0; gsub(/^[[:space:]]+|[[:space:]]+$/, "", line) }
    inside && line ~ /^## / { exit }
    inside && line != "" { print line }
    !inside && line == target { inside = 1 }
  ' "$ABS_NOTES"
}

none_re='^-[[:space:]]*None'

section_has_changes() {
  local entry
  while IFS= read -r entry; do
    if [[ ! "$entry" =~ $none_re ]]; then
      return 0
    fi
  done < <(section_entries "$1")
  return 1
}

ensure_changelog() {
  local path="$ROOT_DIR/$1"
  if [[ ! -f "$path" ]]; then
    echo "Missing changelog file: $1" >&2
    exit 1
  fi
  if ! grep -qF "## $VERSION" "$path"; then
    echo "Changelog $1 missing entry for $VERSION." >&2
    exit 1
  fi
}

if section_has_changes Core; then ensure_changelog notes/changelog/core.md; fi
if section_has_changes GUI; then ensure_changelog notes/changelog/gui.md; fi
if section_has_changes Installer; then ensure_changelog notes/changelog/installer.md; fi
if section_has_changes Tooling; then ensure_changelog notes/changelog/tooling.md; fi

format_re='^-[[:space:]]*([A-Za-z0-9 _.-]+):'
while IFS= read -r entry; do
  if [[ "$entry" =~ $none_re ]]; then
    continue
  fi
  if [[ ! "$entry" =~ $format_re ]]; then
    echo "Format bullet must look like '- Name: details'. Offending entry: $entry" >&2
    exit 1
  fi
  slug="$(sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' <<<"${BASH_REMATCH[1]}" | tr '[:upper:] ' '[:lower:]-')"
  ensure_changelog "notes/changelog/formats/${slug}.md"
done < <(section_entries Formats)

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
