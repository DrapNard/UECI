#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENGINE_DIR="${UECI_ENGINE_DIR:-/tmp/ueci-plugin-vfs-work}"
OUTPUT_DIR="${UECI_PLUGIN_OUTPUT:-/tmp/ueci-plugin-vfs-package}"
PLUGIN="${UECI_PLUGIN:-$ROOT/fixtures/MinimalPlugin/UECIMinimal.uplugin}"
REF="${UECI_EPIC_REF:-release}"
VERBOSE="${UECI_VFS_VERBOSE:-0}"

if [[ -z "${UECI_EPIC_GITHUB_TOKEN:-}" ]]; then
  echo "UECI_EPIC_GITHUB_TOKEN is required for the mounted plugin smoke test." >&2
  exit 2
fi

cd "$ROOT"
dotnet build Ueci.sln -c Release

args=(
  build-plugin "$PLUGIN"
  --engine-dir "$ENGINE_DIR"
  --out "$OUTPUT_DIR"
  --ref "$REF"
  --backend fuse
  --no-pack-cache
)
if [[ "$VERBOSE" != "0" ]]; then
  args+=(--vfs-verbose)
fi

echo "[smoke] building minimal plugin through the mounted Engine backend" >&2
echo "[smoke] workspace=$ENGINE_DIR output=$OUTPUT_DIR plugin=$PLUGIN ref=$REF" >&2

dotnet run --project src/Ueci.Cli/Ueci.Cli.csproj -c Release --no-build -- "${args[@]}"

descriptor="$OUTPUT_DIR/UECIMinimal/UECIMinimal.uplugin"
if [[ ! -f "$descriptor" ]]; then
  echo "[smoke] packaged plugin descriptor is missing: $descriptor" >&2
  exit 1
fi

if ! find "$OUTPUT_DIR/UECIMinimal/Binaries" -type f \( -name '*.so' -o -name '*.dll' -o -name '*.dylib' \) -print -quit 2>/dev/null | grep -q .; then
  echo "[smoke] mounted plugin build produced no native plugin binary." >&2
  find "$OUTPUT_DIR/UECIMinimal" -maxdepth 4 -type f -print >&2 || true
  exit 1
fi

echo "Mounted plugin build smoke OK: $OUTPUT_DIR/UECIMinimal"
