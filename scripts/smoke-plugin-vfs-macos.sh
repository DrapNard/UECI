#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_VOLUME="${UECI_PROJECT_VOLUME_ROOT:-/Volumes/Project}"
ENGINE_DIR="${UECI_ENGINE_DIR:-$ROOT/.ueci/smoke-plugin-vfs-macos-work}"
OUTPUT_DIR="${UECI_PLUGIN_OUTPUT:-$ROOT/.ueci/smoke-plugin-vfs-macos-package}"
PLUGIN="${UECI_PLUGIN:-$ROOT/fixtures/MinimalPlugin/UECIMinimal.uplugin}"
REF="${UECI_EPIC_REF:-5.5}"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "macOS/macFUSE smoke must run on macOS." >&2
  exit 2
fi
if [[ -z "${UECI_EPIC_GITHUB_TOKEN:-}" ]]; then
  echo "UECI_EPIC_GITHUB_TOKEN is required for the macOS mounted-plugin smoke test." >&2
  exit 2
fi
if ! command -v pkg-config >/dev/null || ! pkg-config --exists fuse3; then
  echo "macFUSE development metadata (pkg-config fuse3) is required." >&2
  exit 2
fi
if ! xcode-select -p >/dev/null 2>&1; then
  echo "Xcode command-line tools are required for Unreal macOS builds." >&2
  exit 2
fi
case "$(cd "$(dirname "$ENGINE_DIR")" && pwd)" in
  "$PROJECT_VOLUME"/*) ;;
  *) echo "Engine workspace must be under $PROJECT_VOLUME: $ENGINE_DIR" >&2; exit 2 ;;
esac
case "$(cd "$(dirname "$OUTPUT_DIR")" && pwd)" in
  "$PROJECT_VOLUME"/*) ;;
  *) echo "Package output must be under $PROJECT_VOLUME: $OUTPUT_DIR" >&2; exit 2 ;;
esac

cd "$ROOT"
dotnet build Ueci.sln -c Release

started=$SECONDS
dotnet run --project src/Ueci.Cli/Ueci.Cli.csproj -c Release --no-build -- \
  build-plugin "$PLUGIN" \
  --engine-dir "$ENGINE_DIR" \
  --out "$OUTPUT_DIR" \
  --ref "$REF" \
  --backend fuse \
  --cache-dir "$ROOT/.ueci/cache" \
  --no-pack-cache
elapsed=$((SECONDS - started))

descriptor="$OUTPUT_DIR/UECIMinimal/UECIMinimal.uplugin"
binary="$OUTPUT_DIR/UECIMinimal/Binaries/Mac/UECIMinimal.dylib"
[[ -f "$descriptor" ]] || { echo "Missing package descriptor: $descriptor" >&2; exit 1; }
[[ -f "$binary" ]] || { echo "Missing native macOS plugin binary: $binary" >&2; exit 1; }
(( elapsed < 300 )) || { echo "Cold minimal-plugin build exceeded 5 minutes: ${elapsed}s" >&2; exit 1; }
echo "macOS mounted-plugin smoke OK in ${elapsed}s: $OUTPUT_DIR/UECIMinimal"
