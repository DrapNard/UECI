#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "usage: $0 <Commit.gitdeps.xml> <engine-path> [output]" >&2
  exit 2
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MANIFEST="$1"
ENGINE_PATH="$2"
OUTPUT="${3:-$(mktemp -u /tmp/ueci-fetch-XXXXXX)}"

cd "$ROOT"
dotnet build Ueci.sln -c Release
dotnet run --project src/Ueci.Cli/Ueci.Cli.csproj -c Release --no-build -- \
  gitdeps fetch "$MANIFEST" "$ENGINE_PATH" --out "$OUTPUT" --no-pack-cache

echo "Fetched to: $OUTPUT"
if command -v file >/dev/null 2>&1; then
  file "$OUTPUT"
fi
