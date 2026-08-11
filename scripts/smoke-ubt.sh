#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${UECI_EPIC_GITHUB_TOKEN:-}" ]]; then
  echo "UECI_EPIC_GITHUB_TOKEN is required" >&2
  exit 2
fi

root="${UECI_UBT_ROOT:-/tmp/ueci-ubt-bootstrap}"

exec dotnet run --project src/Ueci.Cli/Ueci.Cli.csproj -c Release -- \
  ubt bootstrap \
  --dir "$root" \
  --no-pack-cache \
  "$@"
