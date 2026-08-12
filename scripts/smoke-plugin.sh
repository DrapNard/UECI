#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENGINE_DIR="${UECI_ENGINE_DIR:-/tmp/ueci-plugin-engine}"
OUTPUT_DIR="${UECI_PLUGIN_OUTPUT:-/tmp/ueci-plugin-package}"
PLUGIN="${UECI_PLUGIN:-$ROOT/fixtures/MinimalPlugin/UECIMinimal.uplugin}"
REF="${UECI_EPIC_REF:-release}"

if [[ -z "${UECI_EPIC_GITHUB_TOKEN:-}" ]]; then
  echo "UECI_EPIC_GITHUB_TOKEN is required for the real plugin smoke test." >&2
  exit 2
fi

exec dotnet run --project "$ROOT/src/Ueci.Cli/Ueci.Cli.csproj" -- \
  build-plugin "$PLUGIN" \
  --engine-dir "$ENGINE_DIR" \
  --out "$OUTPUT_DIR" \
  --ref "$REF" \
  --no-pack-cache
